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
public enum StartupRegistrationTestStatus { None, Started, PresentationReady, AlreadyRunning, Failed, TimedOut, CleanupFailed }
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
    public DateTimeOffset? ExitingAt { get; init; }
    public Guid? StartupTestId { get; init; }
    public StartupRegistrationTestStatus StartupTestResult { get; init; }
    public StartupRegistrationTestStatus StartupTestExecutionResult { get; init; }
    public int NotificationRecoveryCount { get; init; }
    public DateTimeOffset? NotificationRecoveredAt { get; init; }
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
    private readonly long _origin;
    private StartupHealthRecord _current;
    private Task _writeTail = Task.CompletedTask;
    private string? _lastPersistenceDiagnostic;

    public StartupHealthJournal(string path, TimeProvider timeProvider,
        DateTimeOffset? processStartedAt = null, long? monotonicOrigin = null,
        DateTimeOffset? instanceReadyAt = null, long? instanceReadyTimestamp = null)
    {
        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider;
        _origin = monotonicOrigin ?? Stopwatch.GetTimestamp();
        _current = new StartupHealthRecord
        {
            ProcessStartedAt = processStartedAt ?? timeProvider.GetUtcNow(),
            InstanceReadyAt = instanceReadyAt,
            ElapsedMilliseconds = instanceReadyTimestamp is { } readyTimestamp
                ? new Dictionary<string, long>
                {
                    [StartupPhase.InstanceReady.ToString()] =
                        (long)Stopwatch.GetElapsedTime(_origin, readyTimestamp).TotalMilliseconds
                }
                : new Dictionary<string, long>()
        };
    }

    public StartupHealthRecord Current { get { lock (_sync) return _current; } }
    public string? LastPersistenceDiagnostic { get { lock (_sync) return _lastPersistenceDiagnostic; } }

    public void SetSource(StartupLaunchSource source) => Mutate(record => record with { Source = source });
    public void SetStartupTest(Guid? testId, StartupRegistrationTestStatus status) =>
        Mutate(record => record with { StartupTestId = testId, StartupTestResult = status });
    public void RecordNotificationRecovery() => Mutate(record => record with
    {
        NotificationRecoveryCount = record.NotificationRecoveryCount + 1,
        NotificationRecoveredAt = _timeProvider.GetUtcNow()
    });

    public void RecordStartupTestResult(Guid testId, StartupRegistrationTestStatus status)
    {
        var now = _timeProvider.GetUtcNow();
        var snapshot = new StartupHealthRecord
        {
            AttemptId = testId,
            Source = StartupLaunchSource.StartupTest,
            ProcessStartedAt = now,
            StartupTestId = testId,
            StartupTestResult = status,
            StartupTestExecutionResult = status is StartupRegistrationTestStatus.CleanupFailed
                ? StartupRegistrationTestStatus.None : status,
            ReadyAt = status is StartupRegistrationTestStatus.PresentationReady or
                StartupRegistrationTestStatus.AlreadyRunning ? now : null
        };
        lock (_sync)
            _writeTail = _writeTail.ContinueWith(_ => PersistSnapshotAsync(snapshot), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    public void Mark(StartupPhase phase, string? diagnosticCode = null, int? exitCode = null) =>
        Mark(phase, diagnosticCode is null ? StartupPhaseOutcome.Succeeded : StartupPhaseOutcome.Degraded,
            diagnosticCode, exitCode);

    public void Mark(StartupPhase phase, StartupPhaseOutcome outcome, string? diagnosticCode = null, int? exitCode = null)
    {
        var now = _timeProvider.GetUtcNow();
        var elapsed = (long)Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;
        Mutate(record =>
        {
            var elapsedMap = new Dictionary<string, long>(record.ElapsedMilliseconds);
            if (phase != StartupPhase.InstanceReady || !elapsedMap.ContainsKey(phase.ToString()))
                elapsedMap[phase.ToString()] = elapsed;
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
                StartupPhase.InstanceReady => updated with { InstanceReadyAt = updated.InstanceReadyAt ?? now },
                StartupPhase.MinimalServicesReady => updated with { MinimalServicesReadyAt = now },
                StartupPhase.NotificationAreaReady when outcome == StartupPhaseOutcome.Succeeded => updated with { NotificationAreaReadyAt = now },
                StartupPhase.PresentationReady => updated with { PresentationReadyAt = now },
                StartupPhase.RegistrationHealthReady => updated with { RegistrationHealthReadyAt = now },
                StartupPhase.ModuleDiscoveryComplete => updated with { ModuleDiscoveryCompletedAt = now },
                StartupPhase.AuthorizedModulesRestored => updated with { AuthorizedModulesRestoredAt = now },
                StartupPhase.Ready => updated with { ReadyAt = now },
                StartupPhase.Exiting => updated with { ExitingAt = now },
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
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<List<StartupHealthRecord>>(stream, JsonOptions, token) ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { return []; }
    }

    public async Task<StartupHealthRecord?> WaitForStartupTestAsync(
        Guid testId, TimeSpan timeout, CancellationToken token = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(timeout);
        try
        {
            while (!budget.IsCancellationRequested)
            {
                var match = (await ReadAsync(budget.Token)).Where(record => record.StartupTestId == testId)
                    .OrderByDescending(record => record.ProcessStartedAt).FirstOrDefault(record =>
                        record.StartupTestResult is StartupRegistrationTestStatus.PresentationReady or
                            StartupRegistrationTestStatus.AlreadyRunning or StartupRegistrationTestStatus.Failed or
                            StartupRegistrationTestStatus.TimedOut or StartupRegistrationTestStatus.CleanupFailed);
                if (match is not null) return match;
                await Task.Delay(TimeSpan.FromMilliseconds(200), _timeProvider, budget.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        return null;
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
            await using var journalLock = await AcquireJournalLockAsync(directory);
            foreach (var stale in Directory.EnumerateFiles(directory, $"{Path.GetFileName(_path)}.tmp.*"))
                try { if (File.GetLastWriteTimeUtc(stale) < _timeProvider.GetUtcNow().UtcDateTime.AddDays(-1)) File.Delete(stale); } catch (IOException) { }
            var existing = (await ReadAsync()).FirstOrDefault(record => record.AttemptId == snapshot.AttemptId);
            if (existing is not null && snapshot.Source == StartupLaunchSource.StartupTest)
                snapshot = MergeStartupTest(existing, snapshot);
            var all = (await ReadAsync()).Where(record => record.AttemptId != snapshot.AttemptId).Append(snapshot).ToArray();
            var normal = all.Where(record => record.Source != StartupLaunchSource.StartupTest)
                .OrderBy(record => record.ProcessStartedAt).TakeLast(MaximumRecords - 3);
            var tests = all.Where(record => record.Source == StartupLaunchSource.StartupTest)
                .OrderBy(record => record.ProcessStartedAt).TakeLast(3);
            var records = normal.Concat(tests).OrderBy(record => record.ProcessStartedAt).ToArray();
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

    private static StartupHealthRecord MergeStartupTest(StartupHealthRecord current, StartupHealthRecord incoming)
    {
        var currentExecution = current.StartupTestExecutionResult != StartupRegistrationTestStatus.None
            ? current.StartupTestExecutionResult
            : current.StartupTestResult is StartupRegistrationTestStatus.CleanupFailed
                ? StartupRegistrationTestStatus.None : current.StartupTestResult;
        var incomingExecution = incoming.StartupTestExecutionResult;
        var execution = TestStatusRank(incomingExecution) >= TestStatusRank(currentExecution)
            ? incomingExecution : currentExecution;
        var cleanupFailed = current.StartupTestResult == StartupRegistrationTestStatus.CleanupFailed ||
            incoming.StartupTestResult == StartupRegistrationTestStatus.CleanupFailed;
        return incoming with
        {
            AttemptId = current.AttemptId,
            ProcessStartedAt = current.ProcessStartedAt <= incoming.ProcessStartedAt
                ? current.ProcessStartedAt : incoming.ProcessStartedAt,
            StartupTestResult = cleanupFailed ? StartupRegistrationTestStatus.CleanupFailed : execution,
            StartupTestExecutionResult = execution,
            ReadyAt = current.ReadyAt ?? incoming.ReadyAt
        };
    }

    private static int TestStatusRank(StartupRegistrationTestStatus status) => status switch
    {
        StartupRegistrationTestStatus.None => 0,
        StartupRegistrationTestStatus.Started => 1,
        StartupRegistrationTestStatus.PresentationReady or StartupRegistrationTestStatus.AlreadyRunning or
            StartupRegistrationTestStatus.Failed or StartupRegistrationTestStatus.TimedOut => 2,
        _ => 0
    };

    private async Task<FileStream> AcquireJournalLockAsync(string directory)
    {
        var lockPath = Path.Combine(directory, $"{Path.GetFileName(_path)}.lock");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < 40)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), _timeProvider);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await FlushAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }
}
