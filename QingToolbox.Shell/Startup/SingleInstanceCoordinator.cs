using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.IO;

namespace QingToolbox.Shell.Startup;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _serverTask;

    private SingleInstanceCoordinator(Mutex mutex, string pipeName, bool isPrimary)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }
    public event Func<InstanceActivationMessage, Task>? MessageReceived;

    public static SingleInstanceCoordinator Create()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
        var mutex = new Mutex(true, $"QingToolbox.{suffix}.Instance", out var created);
        return new SingleInstanceCoordinator(mutex, $"QingToolbox.{suffix}.Activation", created);
    }

    public void StartServer()
    {
        if (!IsPrimary || _serverTask is not null) return;
        _serverTask = RunServerAsync(_stopping.Token);
    }

    public async Task<bool> SendAsync(InstanceActivationMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(1500, cancellationToken);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), 128, leaveOpen: false);
            await writer.WriteLineAsync(message.ToString().AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8, false, 128, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken);
                if (InstanceActivationProtocol.TryParse(line, out var message) && MessageReceived is { } handler)
                    await handler(message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (IOException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        if (_serverTask is not null)
        {
            try { await _serverTask; } catch (OperationCanceledException) { }
        }
        _stopping.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
