using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using QingToolbox.Core.Updates;

namespace QingToolbox.Shell.Services;

public sealed record ModuleProcessRuntimeState(bool ProcessRunning, int? ProcessId,
    bool HandshakeCompleted, bool ModuleLoaded, bool IsActive, bool HasWindows,
    string ManifestVersion, string ModuleApiVersion, string ProgramTreeIdentity, long RuntimeGeneration,
    string? RuntimeVariant, bool WindowVisible = false);

public sealed class ModuleProcessExitedEventArgs(
    string moduleId, long runtimeGeneration, int processId, int? exitCode,
    bool expected, string failureCode) : EventArgs
{
    public string ModuleId { get; } = moduleId;
    public long RuntimeGeneration { get; } = runtimeGeneration;
    public int ProcessId { get; } = processId;
    public int? ExitCode { get; } = exitCode;
    public bool Expected { get; } = expected;
    public string FailureCode { get; } = failureCode;
}

public sealed class ModuleProcessBroker(ApplicationPaths paths, SessionLogService log) : IAsyncDisposable
{
    private const int ProtocolVersion = 1;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(8);
    private static readonly HashSet<string> AllowedCommands =
        ["GetState", "Activate", "Deactivate", "OpenWindow", "CloseWindow", "SuspendWindow", "RestoreWindow", "Shutdown"];
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private long _generation;
    public string? LastFailureCode { get; private set; }
    public event EventHandler<ModuleProcessExitedEventArgs>? ProcessExited;
    internal ModuleProcessBrokerTestHooks? TestHooks { get; set; }

    public ModuleProcessRuntimeState? GetState(string moduleId) =>
        _sessions.TryGetValue(moduleId, out var session) ? session.State : null;

    public async Task<bool> RestoreAsync(ModuleUpdateRuntimeRestoreRequest request,
        string moduleDirectory, CancellationToken cancellationToken)
    {
        LastFailureCode = null;
        if (_sessions.ContainsKey(request.ModuleId)) { LastFailureCode = "SessionAlreadyExists"; return false; }
        var pipeName = $"QingToolbox.ModuleHost.{Guid.NewGuid():N}";
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var executable = Path.Combine(AppContext.BaseDirectory, "QingToolbox.ModuleHost.exe");
        if (!File.Exists(executable)) { LastFailureCode = "ModuleHostMissing"; server.Dispose(); return false; }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        Add(start, "--pipe", pipeName, "--nonce", nonce, "--module-id", request.ModuleId,
            "--manifest-version", request.ExpectedProgram.ManifestVersion,
            "--module-api", request.ExpectedProgram.ModuleApiVersion,
            "--tree-identity", request.ExpectedProgram.ProgramTreeIdentity,
            "--module-directory", moduleDirectory, "--data-root", paths.ModuleDataDirectory);
        TestHooks?.ConfigureWorkerStart?.Invoke(request.ModuleId, start);
        var process = Process.Start(start) ?? throw new IOException("ModuleHost failed to start.");
        Session? session = null;
        try
        {
            await server.WaitForConnectionAsync(cancellationToken).WaitAsync(OperationTimeout, cancellationToken);
            session = await Session.CreateAsync(server, process, request, nonce,
                Interlocked.Increment(ref _generation), OnSessionExited, cancellationToken);
            var exitedBeforePublication = session.HasExited;
            if (!_sessions.TryAdd(request.ModuleId, session))
            {
                LastFailureCode = "ModuleHost.SessionPublicationFailed";
                session.MarkExpectedExit();
                await session.TerminateAsync();
                await session.DisposeAsync();
                return false;
            }
            TestHooks?.AfterSessionPublishedBeforeExitObservation?.Invoke(request.ModuleId, process);
            var publicationFailureCode = exitedBeforePublication
                ? "ModuleHost.ExitedBeforePublication"
                : "ModuleHost.ExitedDuringRestore";
            var observationActivated = session.ActivateExitObservation(publicationFailureCode);
            if (exitedBeforePublication || !observationActivated ||
                !_sessions.TryGetValue(request.ModuleId, out var current) || !ReferenceEquals(current, session))
            {
                LastFailureCode = exitedBeforePublication
                    ? "ModuleHost.ExitedBeforePublication"
                    : "ModuleHost.ExitedDuringRestore";
                await session.ObserveExitAsync(LastFailureCode);
                return false;
            }
            // A state round-trip closes the loaded-only race: Hello alone is not
            // sufficient proof that the worker survived Session publication.
            if (!await CommandAsync(request.ModuleId, "GetState", cancellationToken))
            {
                LastFailureCode = "ModuleHost.ExitedDuringRestore";
                await session.ObserveExitAsync(LastFailureCode);
                return false;
            }
            if (request.DesiredRuntimeState.IsActive &&
                !await CommandAsync(request.ModuleId, "Activate", cancellationToken))
            {
                LastFailureCode ??= "RestoreActivateFailed";
                await ShutdownAsync(request.ModuleId, CancellationToken.None);
                return false;
            }
            if (session.HasExited || !_sessions.TryGetValue(request.ModuleId, out current) ||
                !ReferenceEquals(current, session))
            {
                LastFailureCode = "ModuleHost.ExitedDuringRestore";
                await session.ObserveExitAsync(LastFailureCode);
                return false;
            }
            if (request.DesiredRuntimeState.HasWindows &&
                !await CommandAsync(request.ModuleId, "OpenWindow", cancellationToken))
            {
                LastFailureCode ??= "RestoreOpenWindowFailed";
                await ShutdownAsync(request.ModuleId, CancellationToken.None);
                return false;
            }
            if (!_sessions.TryGetValue(request.ModuleId, out current) ||
                !ReferenceEquals(current, session) || !session.TryMarkRestoreCompleted())
            {
                LastFailureCode = "ModuleHost.ExitedDuringRestore";
                await session.ObserveExitAsync(LastFailureCode);
                return false;
            }
            log.Information("ModuleProcess", $"ModuleHost restored; module={request.ModuleId}; generation={session.State.RuntimeGeneration}.");
            return true;
        }
        catch (Exception exception)
        {
            LastFailureCode ??= exception is UnauthorizedAccessException
                ? "ModuleHost.ResponseIdentityMismatch"
                : session?.CurrentExitFailureCode ?? $"ModuleHost.{exception.GetType().Name}";
            log.Warning("ModuleProcess", $"ModuleHost restore failed; module={request.ModuleId}; failure={LastFailureCode}.");
            if (session is not null)
                await RemoveAndTerminateAsync(request.ModuleId, session, expected: false, LastFailureCode);
            else
            {
                try { process.Kill(true); } catch { }
                process.Dispose(); server.Dispose();
            }
            return false;
        }
    }

    public async Task<bool> CommandAsync(string moduleId, string command, CancellationToken token)
    {
        if (!AllowedCommands.Contains(command)) throw new InvalidDataException("Unknown ModuleHost command.");
        if (!_sessions.TryGetValue(moduleId, out var session)) return command is "CloseWindow" or "Deactivate";
        try { return await session.CommandAsync(command, token); }
        catch (UnauthorizedAccessException)
        {
            LastFailureCode = "ModuleHost.ResponseIdentityMismatch";
            await RemoveAndTerminateAsync(moduleId, session, expected: false,
                "ModuleHost.ResponseIdentityMismatch");
            return false;
        }
    }

    public async Task<bool> SuspendWindowsAsync(CancellationToken token = default) =>
        await CommandAllAsync("SuspendWindow", token);

    public async Task<bool> RestoreWindowsAsync(CancellationToken token = default) =>
        await CommandAllAsync("RestoreWindow", token);

    private async Task<bool> CommandAllAsync(string command, CancellationToken token)
    {
        var snapshot = _sessions.ToArray();
        TestHooks?.AfterBatchSnapshot?.Invoke(command, snapshot.Select(item => item.Value.State).ToArray());
        var allSucceeded = true;
        foreach (var (moduleId, session) in snapshot)
        {
            var failureCode = "ModuleHost.BatchSessionUnavailable";
            try
            {
                if (!_sessions.TryGetValue(moduleId, out var current) || !ReferenceEquals(current, session))
                {
                    allSucceeded = false;
                    LogBatchFailure(moduleId, session, command, failureCode);
                    continue;
                }

                if (!await session.CommandAsync(command, token))
                {
                    allSucceeded = false;
                    failureCode = "ModuleHost.BatchCommandRejected";
                    LogBatchFailure(moduleId, session, command, failureCode);
                }
            }
            catch (Exception exception)
            {
                allSucceeded = false;
                failureCode = exception is UnauthorizedAccessException
                    ? "ModuleHost.ResponseIdentityMismatch"
                    : $"ModuleHost.{exception.GetType().Name}";
                LogBatchFailure(moduleId, session, command, failureCode);
                if (exception is UnauthorizedAccessException)
                    await RemoveAndTerminateAsync(moduleId, session, expected: false, failureCode);
            }
        }
        return allSucceeded;
    }

    private void LogBatchFailure(string moduleId, Session session, string command, string failureCode) =>
        log.Warning("ModuleProcess",
            $"Batch command failed; command={command}; module={moduleId}; generation={session.State.RuntimeGeneration}; failure={failureCode}.");

    public async Task<bool> ShutdownAsync(string moduleId, CancellationToken token)
    {
        if (!_sessions.TryGetValue(moduleId, out var session)) return true;
        session.MarkExpectedExit();
        var exited = await session.ShutdownAsync(token);
        if (exited && _sessions.TryRemove(new(moduleId, session)))
            await session.DisposeAsync();
        return exited && !_sessions.ContainsKey(moduleId);
    }

    public bool VerifyExited(string moduleId) => !_sessions.TryGetValue(moduleId, out var session) || session.HasExited;
    public bool HasSession(string moduleId) => _sessions.ContainsKey(moduleId);

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToArray()) await ShutdownAsync(id, CancellationToken.None);
    }

    private async Task OnSessionExited(Session session, string failureCode)
    {
        try
        {
            _sessions.TryRemove(new(session.ModuleId, session));
            var args = session.CreateExitEventArgs(failureCode);
            await session.DisposeAsync();
            log.Warning("ModuleProcess", $"ModuleHost exited; module={args.ModuleId}; generation={args.RuntimeGeneration}; expected={args.Expected}; code={args.ExitCode}.");
            ProcessExited?.Invoke(this, args);
        }
        catch (Exception exception)
        {
            log.Error("ModuleProcess", "ModuleHost exit cleanup failed.", exception);
        }
    }

    private async Task RemoveAndTerminateAsync(
        string moduleId, Session session, bool expected = true, string? failureCode = null)
    {
        if (expected) session.MarkExpectedExit();
        else if (failureCode is not null) session.SetExitFailureCode(failureCode);
        _sessions.TryRemove(new(moduleId, session));
        await session.TerminateAsync();
        await session.ObserveExitAsync(failureCode ??
            (expected ? "ModuleHost.ExpectedExit" : "ModuleHost.UnexpectedExit"));
        await session.DisposeAsync();
    }

    private static void Add(ProcessStartInfo start, params string[] values)
    { foreach (var value in values) start.ArgumentList.Add(value); }

    internal static bool IsAllowedCommand(string command) => AllowedCommands.Contains(command);

    internal static bool IsValidHandshake(
        int protocolVersion, string nonce, string moduleId, string manifestVersion,
        string moduleApiVersion, string programTreeIdentity, int processId,
        string expectedNonce, ModuleUpdateRuntimeRestoreRequest request, int expectedProcessId) =>
        protocolVersion == ProtocolVersion && nonce == expectedNonce &&
        moduleId == request.ModuleId && manifestVersion == request.ExpectedProgram.ManifestVersion &&
        moduleApiVersion == request.ExpectedProgram.ModuleApiVersion &&
        programTreeIdentity == request.ExpectedProgram.ProgramTreeIdentity && processId == expectedProcessId;

    internal static async Task<bool> WaitForExitOrKillAsync(Process process, TimeSpan timeout, CancellationToken token)
    {
        if (HasExitedOrDisposed(process)) return true;
        try { await process.WaitForExitAsync(token).WaitAsync(timeout, token); }
        catch
        {
            if (HasExitedOrDisposed(process)) return true;
            try { process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(timeout); }
            catch { return HasExitedOrDisposed(process); }
        }
        return HasExitedOrDisposed(process);
    }

    private static bool HasExitedOrDisposed(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly Process _process;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly string _nonce;
        private readonly SemaphoreSlim _commands = new(1, 1);
        private readonly Func<Session, string, Task> _exited;
        private readonly TaskCompletionSource _exitHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        private int _expectedExit;
        private int _exitPublished;
        private int _exitObservationActive;
        private readonly object _exitStateGate = new();
        private string _exitFailureCode = "ModuleHost.ExitedDuringRestore";
        public string ModuleId { get; }
        public ModuleProcessRuntimeState State { get; private set; }
        public string CurrentExitFailureCode
        {
            get { lock (_exitStateGate) return _exitFailureCode; }
        }
        public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }

        private Session(NamedPipeServerStream pipe, Process process, string nonce, string moduleId,
            ModuleProcessRuntimeState state, Func<Session, string, Task> exited)
        {
            _pipe = pipe; _process = process; _nonce = nonce; ModuleId = moduleId; State = state; _exited = exited;
            _reader = new(pipe, leaveOpen: true); _writer = new(pipe, leaveOpen: true) { AutoFlush = true };
        }

        public static async Task<Session> CreateAsync(NamedPipeServerStream pipe, Process process,
            ModuleUpdateRuntimeRestoreRequest request, string nonce, long generation,
            Func<Session, string, Task> exited, CancellationToken token)
        {
            var session = new Session(pipe, process, nonce, request.ModuleId, new(true, process.Id, false, false, false, false,
                request.ExpectedProgram.ManifestVersion, request.ExpectedProgram.ModuleApiVersion,
                request.ExpectedProgram.ProgramTreeIdentity, generation, null), exited);
            var hello = await session.ReadAsync(token);
            if (hello.Type != "Hello" || !session.IsValidIdentity(hello))
                throw new UnauthorizedAccessException("ModuleHost handshake rejected.");
            session.State = session.State with { HandshakeCompleted = true, ModuleLoaded = true,
                RuntimeVariant = hello.RuntimeVariant };
            return session;
        }

        public async Task<bool> CommandAsync(string command, CancellationToken token)
        {
            if (!IsAllowedCommand(command)) throw new InvalidDataException("Unknown ModuleHost command.");
            await _commands.WaitAsync(token);
            try
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(new Message(ProtocolVersion, command, _nonce, ModuleId,
                    State.ManifestVersion, State.ModuleApiVersion, State.ProgramTreeIdentity, State.ProcessId ?? 0,
                    State.IsActive, State.HasWindows, State.RuntimeVariant)));
                var response = await ReadAsync(token);
                if (response.Type != "State" || !IsValidIdentity(response))
                    throw new UnauthorizedAccessException("ModuleHost state response identity rejected.");
                State = State with { IsActive = response.IsActive, HasWindows = response.HasWindows,
                    WindowVisible = response.WindowVisible };
                return true;
            }
            finally { _commands.Release(); }
        }

        private bool IsValidIdentity(Message response) =>
            response.ProtocolVersion == ProtocolVersion && response.Nonce == _nonce &&
            response.ModuleId == ModuleId && response.ManifestVersion == State.ManifestVersion &&
            response.ModuleApiVersion == State.ModuleApiVersion &&
            response.ProgramTreeIdentity == State.ProgramTreeIdentity && response.ProcessId == State.ProcessId;

        public void MarkExpectedExit() => Interlocked.Exchange(ref _expectedExit, 1);
        public void SetExitFailureCode(string failureCode)
        {
            lock (_exitStateGate) _exitFailureCode = failureCode;
        }

        public bool ActivateExitObservation(string failureCode)
        {
            lock (_exitStateGate) _exitFailureCode = failureCode;
            if (Interlocked.Exchange(ref _exitObservationActive, 1) == 0)
            {
                _process.Exited += OnExited;
                _process.EnableRaisingEvents = true;
            }
            if (HasExited) _ = ObserveExitAsync(DefaultExitFailureCode());
            return !HasExited;
        }

        public bool TryMarkRestoreCompleted()
        {
            lock (_exitStateGate)
            {
                if (HasExited || Volatile.Read(ref _exitPublished) != 0) return false;
                _exitFailureCode = "ModuleHost.UnexpectedExit";
                return true;
            }
        }

        public Task ObserveExitAsync(string failureCode)
        {
            if (Interlocked.Exchange(ref _exitPublished, 1) == 0)
                _ = CompleteExitAsync(failureCode);
            return _exitHandled.Task;
        }

        private async Task CompleteExitAsync(string failureCode)
        {
            try { await _exited(this, failureCode); }
            finally { _exitHandled.TrySetResult(); }
        }

        private string DefaultExitFailureCode()
        {
            if (Volatile.Read(ref _expectedExit) != 0) return "ModuleHost.ExpectedExit";
            lock (_exitStateGate) return _exitFailureCode;
        }

        public async Task<bool> ShutdownAsync(CancellationToken token)
        {
            try { await CommandAsync("Shutdown", token); } catch { }
            return await WaitForExitOrKillAsync(_process, OperationTimeout, token);
        }

        public async Task TerminateAsync()
        {
            if (HasExited) return;
            try { _process.Kill(true); } catch { }
            try { await _process.WaitForExitAsync().WaitAsync(OperationTimeout); } catch { }
        }

        public ModuleProcessExitedEventArgs CreateExitEventArgs(string failureCode)
        {
            int? exitCode = null;
            try { exitCode = _process.ExitCode; } catch { }
            return new(ModuleId, State.RuntimeGeneration, State.ProcessId ?? 0, exitCode,
                Volatile.Read(ref _expectedExit) != 0, failureCode);
        }

        private void OnExited(object? sender, EventArgs args)
        {
            _ = ObserveExitAsync(DefaultExitFailureCode());
        }

        private async Task<Message> ReadAsync(CancellationToken token)
        {
            var line = await _reader.ReadLineAsync(token).AsTask().WaitAsync(OperationTimeout, token);
            if (line is null || line.Length > 16 * 1024) throw new InvalidDataException("Invalid ModuleHost message.");
            return JsonSerializer.Deserialize<Message>(line) ?? throw new InvalidDataException("Invalid ModuleHost JSON.");
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            if (Volatile.Read(ref _exitObservationActive) != 0) _process.Exited -= OnExited;
            try { _writer.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            try { _reader.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            try { _pipe.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            try { _process.Dispose(); } catch (InvalidOperationException) { }
            _commands.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record Message(int ProtocolVersion, string Type, string Nonce, string ModuleId, string ManifestVersion,
        string ModuleApiVersion, string ProgramTreeIdentity, int ProcessId, bool IsActive, bool HasWindows,
        string? RuntimeVariant, bool WindowVisible = false);
}

internal sealed class ModuleProcessBrokerTestHooks
{
    public Action<string, ProcessStartInfo>? ConfigureWorkerStart { get; init; }
    public Action<string, Process>? AfterSessionPublishedBeforeExitObservation { get; init; }
    public Action<string, IReadOnlyList<ModuleProcessRuntimeState>>? AfterBatchSnapshot { get; init; }
}
