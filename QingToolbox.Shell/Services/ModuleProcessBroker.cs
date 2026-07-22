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
        ["Activate", "Deactivate", "OpenWindow", "CloseWindow", "SuspendWindow", "RestoreWindow", "Shutdown"];
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private long _generation;
    public string? LastFailureCode { get; private set; }
    public event EventHandler<ModuleProcessExitedEventArgs>? ProcessExited;

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
        var process = Process.Start(start) ?? throw new IOException("ModuleHost failed to start.");
        Session? session = null;
        try
        {
            await server.WaitForConnectionAsync(cancellationToken).WaitAsync(OperationTimeout, cancellationToken);
            session = await Session.CreateAsync(server, process, request, nonce,
                Interlocked.Increment(ref _generation), OnSessionExited, cancellationToken);
            if (!_sessions.TryAdd(request.ModuleId, session))
            {
                session.MarkExpectedExit();
                await session.ShutdownAsync(CancellationToken.None);
                LastFailureCode = "SessionAlreadyExists";
                return false;
            }
            if (request.DesiredRuntimeState.IsActive &&
                !await CommandAsync(request.ModuleId, "Activate", cancellationToken))
            {
                LastFailureCode ??= "RestoreActivateFailed";
                await ShutdownAsync(request.ModuleId, CancellationToken.None);
                return false;
            }
            if (request.DesiredRuntimeState.HasWindows &&
                !await CommandAsync(request.ModuleId, "OpenWindow", cancellationToken))
            {
                LastFailureCode ??= "RestoreOpenWindowFailed";
                await ShutdownAsync(request.ModuleId, CancellationToken.None);
                return false;
            }
            log.Information("ModuleProcess", $"ModuleHost restored; module={request.ModuleId}; generation={session.State.RuntimeGeneration}.");
            return true;
        }
        catch (Exception exception)
        {
            LastFailureCode = exception is UnauthorizedAccessException ? "ResponseIdentityMismatch" : exception.GetType().Name;
            log.Warning("ModuleProcess", $"ModuleHost restore failed; module={request.ModuleId}; failure={LastFailureCode}.");
            if (session is not null) await RemoveAndTerminateAsync(request.ModuleId, session);
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
            LastFailureCode = "ResponseIdentityMismatch";
            await RemoveAndTerminateAsync(moduleId, session);
            return false;
        }
    }

    public async Task<bool> SuspendWindowsAsync(CancellationToken token = default) =>
        await CommandAllAsync("SuspendWindow", token);

    public async Task<bool> RestoreWindowsAsync(CancellationToken token = default) =>
        await CommandAllAsync("RestoreWindow", token);

    private async Task<bool> CommandAllAsync(string command, CancellationToken token)
    {
        foreach (var moduleId in _sessions.Keys.ToArray())
            if (!await CommandAsync(moduleId, command, token)) return false;
        return true;
    }

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

    private async void OnSessionExited(Session session)
    {
        try
        {
            if (!_sessions.TryRemove(new(session.ModuleId, session))) return;
            var args = session.CreateExitEventArgs();
            await session.DisposeAsync();
            log.Warning("ModuleProcess", $"ModuleHost exited; module={args.ModuleId}; generation={args.RuntimeGeneration}; expected={args.Expected}; code={args.ExitCode}.");
            ProcessExited?.Invoke(this, args);
        }
        catch (Exception exception)
        {
            log.Error("ModuleProcess", "ModuleHost exit cleanup failed.", exception);
        }
    }

    private async Task RemoveAndTerminateAsync(string moduleId, Session session)
    {
        session.MarkExpectedExit();
        _sessions.TryRemove(new(moduleId, session));
        await session.TerminateAsync();
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
        private readonly Action<Session> _exited;
        private int _disposed;
        private int _expectedExit;
        private int _exitPublished;
        public string ModuleId { get; }
        public ModuleProcessRuntimeState State { get; private set; }
        public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }

        private Session(NamedPipeServerStream pipe, Process process, string nonce, string moduleId,
            ModuleProcessRuntimeState state, Action<Session> exited)
        {
            _pipe = pipe; _process = process; _nonce = nonce; ModuleId = moduleId; State = state; _exited = exited;
            _reader = new(pipe, leaveOpen: true); _writer = new(pipe, leaveOpen: true) { AutoFlush = true };
            _process.EnableRaisingEvents = true;
            _process.Exited += OnExited;
        }

        public static async Task<Session> CreateAsync(NamedPipeServerStream pipe, Process process,
            ModuleUpdateRuntimeRestoreRequest request, string nonce, long generation,
            Action<Session> exited, CancellationToken token)
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

        public ModuleProcessExitedEventArgs CreateExitEventArgs()
        {
            int? exitCode = null;
            try { exitCode = _process.ExitCode; } catch { }
            return new(ModuleId, State.RuntimeGeneration, State.ProcessId ?? 0, exitCode,
                Volatile.Read(ref _expectedExit) != 0,
                Volatile.Read(ref _expectedExit) != 0 ? "ModuleHost.ExpectedExit" : "ModuleHost.UnexpectedExit");
        }

        private void OnExited(object? sender, EventArgs args)
        {
            if (Interlocked.Exchange(ref _exitPublished, 1) == 0) _exited(this);
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
            _process.Exited -= OnExited;
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
