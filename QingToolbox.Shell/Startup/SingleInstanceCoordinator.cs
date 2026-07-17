using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace QingToolbox.Shell.Startup;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    public const string SuccessAcknowledgment = "OK";
    public const string ErrorAcknowledgment = "ERROR";
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500)
    ];

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _serverTask;
    private int _disposed;

    private SingleInstanceCoordinator(Mutex mutex, string pipeName, bool isPrimary)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }
    public event Func<InstanceActivationMessage, Task>? MessageReceived;
    public event Func<InstanceActivationRequest, Task>? RequestReceived;

    public static SingleInstanceCoordinator Create()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return CreateWithSuffix(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16]);
    }

    public static SingleInstanceCoordinator CreateForScope(string scope)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sid}\0{scope}")))[..16];
        return CreateWithSuffix(suffix);
    }

    private static SingleInstanceCoordinator CreateWithSuffix(string suffix)
    {
        var mutex = new Mutex(true, $"QingToolbox.{suffix}.Instance", out var created);
        return new SingleInstanceCoordinator(mutex, $"QingToolbox.{suffix}.Activation", created);
    }

    public void StartServer()
    {
        if (!IsPrimary || _serverTask is not null) return;
        // Construct the first server synchronously so ACL/name/handle failures are
        // reported before startup proceeds to settings and service initialization.
        var initialServer = CreateServer();
        _serverTask = RunServerAsync(initialServer, _stopping.Token);
    }

    public async Task<bool> SendAsync(InstanceActivationMessage message, CancellationToken cancellationToken = default)
        => await SendAsync(new InstanceActivationRequest(message), cancellationToken);

    public async Task<bool> SendAsync(InstanceActivationRequest request, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var attempt = 0;
        while (!budget.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(500, budget.Token);
                using var reader = new StreamReader(client, Encoding.UTF8, false, 128, leaveOpen: true);
                await using var writer = new StreamWriter(client, new UTF8Encoding(false), 128, leaveOpen: true)
                    { AutoFlush = true, NewLine = "\n" };
                var payload = InstanceActivationProtocol.Serialize(request);
                await writer.WriteLineAsync(payload.AsMemory(), budget.Token);
                var acknowledgment = await reader.ReadLineAsync(budget.Token);
                return string.Equals(acknowledgment, SuccessAcknowledgment, StringComparison.Ordinal);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
            {
                if (budget.IsCancellationRequested) break;
                var delay = RetryDelays[Math.Min(attempt++, RetryDelays.Length - 1)];
                await Task.Delay(delay, budget.Token);
            }
        }
        return false;
    }

    private NamedPipeServerStream CreateServer() => new(_pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task RunServerAsync(NamedPipeServerStream initialServer, CancellationToken cancellationToken)
    {
        NamedPipeServerStream? nextServer = initialServer;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = nextServer ?? CreateServer();
                nextServer = null;
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8, false, 128, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), 128, leaveOpen: true)
                    { AutoFlush = true, NewLine = "\n" };
                await ProcessConnectionAsync(reader, writer, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine($"Single-instance connection failed: {exception.GetType().Name}");
            }
        }
    }

    private async Task ProcessConnectionAsync(
        StreamReader reader,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        var line = await ReadBoundedMessageAsync(reader, cancellationToken);
        if (!InstanceActivationProtocol.TryParseRequest(line, out var request))
        {
            await TryWriteAcknowledgmentAsync(writer, ErrorAcknowledgment, cancellationToken);
            return;
        }

        var requestHandler = RequestReceived;
        var handler = MessageReceived;
        if (handler is null && requestHandler is null)
        {
            await TryWriteAcknowledgmentAsync(
                writer,
                request.Message == InstanceActivationMessage.StartupProbe
                    ? SuccessAcknowledgment
                    : ErrorAcknowledgment,
                cancellationToken);
            return;
        }

        // Queue the accepted dispatch before acknowledging it. UI work and handler
        // failures are intentionally decoupled from the pipe connection lifetime.
        if (requestHandler is not null)
            foreach (Func<InstanceActivationRequest, Task> subscriber in requestHandler.GetInvocationList())
                ObserveDispatchTask(Task.Run(async () => await subscriber(request)));
        if (handler is not null)
            foreach (Func<InstanceActivationMessage, Task> subscriber in handler.GetInvocationList())
                ObserveDispatchTask(Task.Run(async () => await subscriber(request.Message)));
        await TryWriteAcknowledgmentAsync(writer, SuccessAcknowledgment, cancellationToken);
    }

    private static async Task<string?> ReadBoundedMessageAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[InstanceActivationProtocol.MaximumMessageLength + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(length, 1), cancellationToken);
            if (count == 0) return null;
            var character = buffer[length];
            if (character == '\n') return new string(buffer, 0, length);
            if (character == '\r') return null;
            length++;
        }

        return null;
    }

    private static async Task TryWriteAcknowledgmentAsync(
        StreamWriter writer,
        string acknowledgment,
        CancellationToken cancellationToken)
    {
        try { await writer.WriteLineAsync(acknowledgment.AsMemory(), cancellationToken); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Single-instance acknowledgment failed: {exception.GetType().Name}");
        }
    }

    private static void ObserveDispatchTask(Task task)
    {
        _ = task.ContinueWith(
            completed => System.Diagnostics.Debug.WriteLine(
                $"Single-instance activation handler failed: {completed.Exception?.GetBaseException().GetType().Name}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        if (_serverTask is not null)
        {
            try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (TimeoutException)
            {
                System.Diagnostics.Debug.WriteLine("Single-instance server stop timed out.");
            }
        }
        _stopping.Dispose();
        // Closing the process-scoped handle releases the kernel object without
        // relying on async continuations returning to the acquiring thread.
        _mutex.Dispose();
    }
}
