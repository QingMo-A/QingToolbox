using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace QingToolbox.Shell.Startup;

public enum StartupPhase
{
    ProcessEntry, InstanceReady, MinimalServicesReady, NotificationAreaReady, PresentationReady,
    RegistrationHealthReady, ModuleDiscoveryComplete, AuthorizedModulesRestored, Ready, Failed, Exiting
}
public enum StartupPhaseOutcome { NotReached, Succeeded, Degraded, Failed }
public enum StartupExitCode
{
    Success = 0, InvalidArguments = 10, RegistrationFailure = 11,
    FatalInitializationFailure = 12, SingleInstanceDeliveryFailure = 13
}

public sealed record StartupHealthRecord
{
    public int SchemaVersion { get; init; } = 2;
    public Guid AttemptId { get; init; } = Guid.NewGuid();
    public StartupLaunchSource Source { get; init; }
    public DateTimeOffset ProcessStartedAt { get; init; }
    public DateTimeOffset? InstanceReadyAt { get; init; }
    public DateTimeOffset? MinimalServicesReadyAt { get; init; }
    public DateTimeOffset? NotificationAreaReadyAt { get; init; }
    public DateTimeOffset? PresentationReadyAt { get; init; }
    public DateTimeOffset? RegistrationHealthReadyAt { get; init; }
    public DateTimeOffset? ModuleDiscoveryCompletedAt { get; init; }
    public DateTimeOffset? AuthorizedModulesRestoredAt { get; init; }
    public DateTimeOffset? ReadyAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
    public StartupPhase? FailurePhase { get; init; }
    public string? FailureCode { get; init; }
    public int? ProcessExitCode { get; init; }
    public bool WasSecondaryInstance { get; init; }
    public IReadOnlyDictionary<string, long> ElapsedMilliseconds { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, StartupPhaseOutcome> PhaseOutcomes { get; init; } =
        Enum.GetValues<StartupPhase>().ToDictionary(value => value.ToString(), _ => StartupPhaseOutcome.NotReached);
    public IReadOnlyList<string> DiagnosticCodes { get; init; } = [];
}

public sealed class StartupHealthJournal : IAsyncDisposable
{
    public const int MaximumRecords = 10;
    public const int MaximumFileBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private StartupHealthRecord _current;
    private Task _writeTail = Task.CompletedTask;
    private string? _lastPersistenceDiagnostic;

    public StartupHealthJournal(string path, TimeProvider timeProvider)
    {
        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider;
        _current = new StartupHealthRecord { ProcessStartedAt = timeProvider.GetUtcNow() };
    }

    public StartupHealthRecord Current { get { lock (_sync) return _current; } }
    public string? LastPersistenceDiagnostic { get { lock (_sync) return _lastPersistenceDiagnostic; } }

    public void SetSource(StartupLaunchSource source) => Mutate(record => record with { Source = source });

    public void Mark(StartupPhase phase, string? diagnosticCode = null, int? exitCode = null) =>
        Mark(phase, diagnosticCode is null ? StartupPhaseOutcome.Succeeded : StartupPhaseOutcome.Degraded,
            diagnosticCode, exitCode);

    public void Mark(StartupPhase phase, StartupPhaseOutcome outcome, string? diagnosticCode = null, int? exitCode = null)
    {
        var now = _timeProvider.GetUtcNow();
        var elapsed = (long)Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;
        Mutate(record =>
        {
            var elapsedMap = new Dictionary<string, long>(record.ElapsedMilliseconds) { [phase.ToString()] = elapsed };
            var outcomes = new Dictionary<string, StartupPhaseOutcome>(record.PhaseOutcomes) { [phase.ToString()] = outcome };
            var diagnostics = record.DiagnosticCodes.ToList();
            if (!string.IsNullOrWhiteSpace(diagnosticCode) && !diagnostics.Contains(diagnosticCode, StringComparer.Ordinal))
                diagnostics.Add(diagnosticCode);
            var updated = record with
            {
                ElapsedMilliseconds = elapsedMap,
                PhaseOutcomes = outcomes,
                DiagnosticCodes = diagnostics,
                ProcessExitCode = exitCode ?? record.ProcessExitCode
            };
            if (outcome == StartupPhaseOutcome.Failed)
                return updated with { FailedAt = now, FailurePhase = phase, FailureCode = diagnosticCode };
            return phase switch
            {
                StartupPhase.InstanceReady => updated with { InstanceReadyAt = now },
                StartupPhase.MinimalServicesReady => updated with { MinimalServicesReadyAt = now },
                StartupPhase.NotificationAreaReady when outcome == StartupPhaseOutcome.Succeeded => updated with { NotificationAreaReadyAt = now },
                StartupPhase.PresentationReady => updated with { PresentationReadyAt = now },
                StartupPhase.RegistrationHealthReady => updated with { RegistrationHealthReadyAt = now },
                StartupPhase.ModuleDiscoveryComplete => updated with { ModuleDiscoveryCompletedAt = now },
                StartupPhase.AuthorizedModulesRestored => updated with { AuthorizedModulesRestoredAt = now },
                StartupPhase.Ready => updated with { ReadyAt = now },
                _ => updated
            };
        });
    }

    public void Fail(StartupPhase failurePhase, string failureCode, int exitCode) =>
        Mark(failurePhase, StartupPhaseOutcome.Failed, failureCode, exitCode);

    public void MarkSecondary(StartupLaunchSource source, string diagnosticCode, int exitCode)
    {
        Mutate(record => record with
        {
            Source = source,
            WasSecondaryInstance = true,
            FailureCode = diagnosticCode,
            ProcessExitCode = exitCode,
            DiagnosticCodes = record.DiagnosticCodes.Append(diagnosticCode).Distinct(StringComparer.Ordinal).ToArray()
        });
    }

    public async Task<IReadOnlyList<StartupHealthRecord>> ReadAsync(CancellationToken token = default)
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length is <= 0 or > MaximumFileBytes) return [];
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<List<StartupHealthRecord>>(stream, JsonOptions, token) ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { return []; }
    }

    public async Task FlushAsync(CancellationToken token = default)
    {
        Task pending;
        lock (_sync) pending = _writeTail;
        await pending.WaitAsync(token);
    }

    private void Mutate(Func<StartupHealthRecord, StartupHealthRecord> update)
    {
        lock (_sync)
        {
            _current = update(_current);
            var snapshot = _current;
            _writeTail = _writeTail.ContinueWith(_ => PersistSnapshotAsync(snapshot), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task PersistSnapshotAsync(StartupHealthRecord snapshot)
    {
        var directory = Path.GetDirectoryName(_path)!;
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(_path)}.tmp.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var stale in Directory.EnumerateFiles(directory, $"{Path.GetFileName(_path)}.tmp.*"))
                try { if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-1)) File.Delete(stale); } catch (IOException) { }
            var records = (await ReadAsync()).Where(record => record.AttemptId != snapshot.AttemptId)
                .Append(snapshot).TakeLast(MaximumRecords).ToArray();
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, records, JsonOptions);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            if (new FileInfo(temporaryPath).Length > MaximumFileBytes)
                throw new IOException("Startup journal exceeded its size limit.");
            File.Move(temporaryPath, _path, overwrite: true);
            lock (_sync) _lastPersistenceDiagnostic = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            lock (_sync) _lastPersistenceDiagnostic = $"startup.journal.{exception.GetType().Name}";
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await FlushAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }
}
