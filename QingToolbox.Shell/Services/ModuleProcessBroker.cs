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
    string? RuntimeVariant);

public sealed class ModuleProcessBroker(ApplicationPaths paths, SessionLogService log) : IAsyncDisposable
{
    private const int ProtocolVersion = 1;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(8);
    private static readonly HashSet<string> AllowedCommands =
        ["Activate", "Deactivate", "OpenWindow", "CloseWindow", "Shutdown"];
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private long _generation;
    public string? LastFailureCode { get; private set; }

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
        try
        {
            await server.WaitForConnectionAsync(cancellationToken).WaitAsync(OperationTimeout, cancellationToken);
            var session = await Session.CreateAsync(server, process, request, nonce,
                Interlocked.Increment(ref _generation), cancellationToken);
            if (!_sessions.TryAdd(request.ModuleId, session)) { await session.DisposeAsync(); return false; }
            if (request.DesiredRuntimeState.IsActive) await session.CommandAsync("Activate", cancellationToken);
            if (request.DesiredRuntimeState.HasWindows) await session.CommandAsync("OpenWindow", cancellationToken);
            log.Information("ModuleProcess", $"ModuleHost restored; module={request.ModuleId}; generation={session.State.RuntimeGeneration}.");
            return true;
        }
        catch (Exception exception)
        {
            LastFailureCode = exception.GetType().Name;
            log.Warning("ModuleProcess", $"ModuleHost restore failed; module={request.ModuleId}; failure={exception.GetType().Name}.");
            try { process.Kill(true); } catch { }
            process.Dispose(); server.Dispose(); return false;
        }
    }

    public async Task<bool> CommandAsync(string moduleId, string command, CancellationToken token)
    {
        if (!AllowedCommands.Contains(command))
            throw new InvalidDataException("Unknown ModuleHost command.");
        if (!_sessions.TryGetValue(moduleId, out var session)) return command is "CloseWindow" or "Deactivate";
        return await session.CommandAsync(command, token);
    }

    public async Task<bool> ShutdownAsync(string moduleId, CancellationToken token)
    {
        if (!_sessions.TryGetValue(moduleId, out var session)) return true;
        var exited = await session.ShutdownAsync(token);
        if (exited) _sessions.TryRemove(new(moduleId, session));
        return exited;
    }

    public bool VerifyExited(string moduleId) => !_sessions.TryGetValue(moduleId, out var session) || session.HasExited;

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToArray()) await ShutdownAsync(id, CancellationToken.None);
    }

    private static void Add(ProcessStartInfo start, params string[] values)
    { foreach (var value in values) start.ArgumentList.Add(value); }

    internal static bool IsAllowedCommand(string command) => AllowedCommands.Contains(command);

    internal static bool IsValidHandshake(
        int protocolVersion, string nonce, string moduleId, string manifestVersion,
        string moduleApiVersion, string programTreeIdentity, int processId,
        string expectedNonce, ModuleUpdateRuntimeRestoreRequest request, int expectedProcessId) =>
        protocolVersion == ProtocolVersion && nonce == expectedNonce &&
        moduleId == request.ModuleId &&
        manifestVersion == request.ExpectedProgram.ManifestVersion &&
        moduleApiVersion == request.ExpectedProgram.ModuleApiVersion &&
        programTreeIdentity == request.ExpectedProgram.ProgramTreeIdentity &&
        processId == expectedProcessId;

    internal static async Task<bool> WaitForExitOrKillAsync(
        Process process, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            await process.WaitForExitAsync(token).WaitAsync(timeout, token);
        }
        catch
        {
            try { process.Kill(true); } catch { }
            // Once termination starts it is cleanup, not caller work. A cancelled
            // transaction must not skip proving that the worker process exited.
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(timeout); }
            catch { return false; }
        }
        return process.HasExited;
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly Process _process;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly string _nonce;
        private readonly string _moduleId;
        private readonly SemaphoreSlim _commands = new(1, 1);
        private int _disposed;
        public ModuleProcessRuntimeState State { get; private set; }
        public bool HasExited => _process.HasExited;

        private Session(NamedPipeServerStream pipe, Process process, string nonce, string moduleId,
            ModuleProcessRuntimeState state)
        { _pipe = pipe; _process = process; _nonce = nonce; _moduleId = moduleId; State = state;
          _reader = new(pipe, leaveOpen: true); _writer = new(pipe, leaveOpen: true) { AutoFlush = true }; }

        public static async Task<Session> CreateAsync(NamedPipeServerStream pipe, Process process,
            ModuleUpdateRuntimeRestoreRequest request, string nonce, long generation, CancellationToken token)
        {
            var session = new Session(pipe, process, nonce, request.ModuleId, new(true, process.Id, false, false, false, false,
                request.ExpectedProgram.ManifestVersion, request.ExpectedProgram.ModuleApiVersion,
                request.ExpectedProgram.ProgramTreeIdentity, generation, null));
            var hello = await session.ReadAsync(token);
            if (hello.Type != "Hello" || !IsValidHandshake(
                    hello.ProtocolVersion, hello.Nonce, hello.ModuleId, hello.ManifestVersion,
                    hello.ModuleApiVersion, hello.ProgramTreeIdentity, hello.ProcessId,
                    nonce, request, process.Id))
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
                await _writer.WriteLineAsync(JsonSerializer.Serialize(new Message(ProtocolVersion, command, _nonce, _moduleId, State.ManifestVersion,
                    State.ModuleApiVersion, State.ProgramTreeIdentity, State.ProcessId ?? 0, State.IsActive, State.HasWindows,
                    State.RuntimeVariant)));
                var response = await ReadAsync(token);
                if (response.ProtocolVersion != ProtocolVersion || response.Type != "State" ||
                    response.Nonce != _nonce) return false;
                State = State with { IsActive = response.IsActive, HasWindows = response.HasWindows };
                return true;
            }
            finally { _commands.Release(); }
        }

        public async Task<bool> ShutdownAsync(CancellationToken token)
        {
            try { await CommandAsync("Shutdown", token); } catch { }
            var exited = await WaitForExitOrKillAsync(_process, OperationTimeout, token);
            await DisposeAsync();
            return exited;
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
            // A worker normally closes its pipe before exiting. StreamWriter.Dispose may
            // attempt a final flush and report the expected broken pipe; cleanup must still
            // release every handle and allow the broker session to be removed.
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
        string? RuntimeVariant);
}
