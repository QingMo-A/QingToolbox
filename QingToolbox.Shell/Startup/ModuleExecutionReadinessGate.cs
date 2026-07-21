using System.Collections.Concurrent;

namespace QingToolbox.Shell.Startup;

public enum ModuleExecutionReadinessStatus
{
    RecoveryPending,
    Ready,
    BlockedByModuleRecovery,
    BlockedByUnattributedRecoveryFailure
}

public sealed record ModuleExecutionReadiness(
    string ModuleId,
    ModuleExecutionReadinessStatus Status)
{
    public bool CanExecute => Status == ModuleExecutionReadinessStatus.Ready;
}

public sealed record ModuleExecutionGateSnapshot(
    bool RecoveryInspected,
    bool HasUnattributedRecoveryFailure,
    IReadOnlyList<string> BlockedModuleIds);

public sealed record ModuleExecutionGateLogEvent(
    string EventName,
    string ModuleId,
    ModuleExecutionReadinessStatus State,
    string FailureCode);

public sealed class ModuleExecutionBlockedException(
    string moduleId,
    ModuleExecutionReadinessStatus status)
    : InvalidOperationException($"Module execution is unavailable while transaction recovery is in state '{status}'.")
{
    public string ModuleId { get; } = moduleId;
    public ModuleExecutionReadinessStatus Status { get; } = status;
}

public interface IModuleExecutionReadinessGate
{
    Task RecoveryInspected { get; }

    ModuleExecutionReadiness GetReadiness(string moduleId);

    Task WaitForRecoveryInspectionAsync(CancellationToken cancellationToken = default);

    ValueTask<IAsyncDisposable> EnterExecutionAsync(
        string moduleId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes one startup recovery decision and serializes normal execution with
/// Development-only update transactions on a per-module boundary.
/// </summary>
public sealed class ModuleTransactionRecoveryGate
{
    private readonly object _stateSync = new();
    private readonly HashSet<string> _blockedModuleIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _moduleOperations =
        new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _recoveryInspected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _maintenance = new(1, 1);
    private readonly Action<ModuleExecutionGateLogEvent>? _log;
    private bool _hasUnattributedRecoveryFailure;
    private bool _recoveryCompleted;
    private bool _shutdownRequested;

    public ModuleTransactionRecoveryGate(Action<ModuleExecutionGateLogEvent>? log = null)
    {
        _log = log;
        Consumer = new ConsumerView(this);
    }

    public IModuleExecutionReadinessGate Consumer { get; }

    public ModuleExecutionGateSnapshot Snapshot
    {
        get
        {
            lock (_stateSync)
            {
                return new(
                    _recoveryCompleted,
                    _hasUnattributedRecoveryFailure,
                    _blockedModuleIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
            }
        }
    }

    internal void CompleteRecovery(
        IEnumerable<string> blockedModuleIds,
        bool hasUnattributedRecoveryFailure)
    {
        ArgumentNullException.ThrowIfNull(blockedModuleIds);
        lock (_stateSync)
        {
            if (_recoveryCompleted)
            {
                return;
            }

            foreach (var moduleId in blockedModuleIds)
            {
                if (!string.IsNullOrWhiteSpace(moduleId))
                {
                    _blockedModuleIds.Add(moduleId);
                }
            }

            _hasUnattributedRecoveryFailure = hasUnattributedRecoveryFailure;
            _recoveryCompleted = true;
        }

        _recoveryInspected.TrySetResult();
    }

    internal void BlockModule(string moduleId, string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        lock (_stateSync)
        {
            _blockedModuleIds.Add(moduleId);
        }

        _log?.Invoke(new(
            "Module execution blocked by recovery",
            moduleId,
            ModuleExecutionReadinessStatus.BlockedByModuleRecovery,
            failureCode));
    }

    internal async ValueTask<IAsyncDisposable> EnterModuleUpdateAsync(
        string moduleId,
        CancellationToken cancellationToken = default)
    {
        await WaitForRecoveryInspectionAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfBlocked(moduleId);
        var maintenance = await EnterMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfBlocked(moduleId);
            var module = await EnterSerializedOperationAsync(moduleId, cancellationToken).ConfigureAwait(false);
            return new CompositeLease(module, maintenance);
        }
        catch { await maintenance.DisposeAsync(); throw; }
    }

    internal async ValueTask<IAsyncDisposable> EnterDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        await WaitForRecoveryInspectionAsync(cancellationToken).ConfigureAwait(false);
        return await EnterMaintenanceAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IAsyncDisposable?> BeginShutdownAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        lock (_stateSync) _shutdownRequested = true;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await _maintenance.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new SemaphoreLease(_maintenance);
        }
        catch (OperationCanceledException) { return null; }
    }

    private async ValueTask<IAsyncDisposable> EnterMaintenanceAsync(CancellationToken cancellationToken)
    {
        lock (_stateSync)
            if (_shutdownRequested) throw new InvalidOperationException("Application shutdown has started.");
        await _maintenance.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateSync)
        {
            if (!_shutdownRequested) return new SemaphoreLease(_maintenance);
        }
        _maintenance.Release();
        throw new InvalidOperationException("Application shutdown has started.");
    }

    private async ValueTask<IAsyncDisposable> EnterExecutionAsync(
        string moduleId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        await WaitForRecoveryInspectionAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfBlocked(moduleId);
        var lease = await EnterSerializedOperationAsync(moduleId, cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfBlocked(moduleId);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private async ValueTask<IAsyncDisposable> EnterSerializedOperationAsync(
        string moduleId,
        CancellationToken cancellationToken)
    {
        var semaphore = _moduleOperations.GetOrAdd(moduleId, static _ => new(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SemaphoreLease(semaphore);
    }

    private async Task WaitForRecoveryInspectionAsync(CancellationToken cancellationToken)
    {
        await _recoveryInspected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private ModuleExecutionReadiness GetReadiness(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        lock (_stateSync)
        {
            if (!_recoveryCompleted)
            {
                return new(moduleId, ModuleExecutionReadinessStatus.RecoveryPending);
            }

            if (_hasUnattributedRecoveryFailure)
            {
                return new(moduleId,
                    ModuleExecutionReadinessStatus.BlockedByUnattributedRecoveryFailure);
            }

            return new(moduleId, _blockedModuleIds.Contains(moduleId)
                ? ModuleExecutionReadinessStatus.BlockedByModuleRecovery
                : ModuleExecutionReadinessStatus.Ready);
        }
    }

    private void ThrowIfBlocked(string moduleId)
    {
        lock (_stateSync)
            if (_shutdownRequested) throw new InvalidOperationException("Application shutdown has started.");
        var readiness = GetReadiness(moduleId);
        if (readiness.CanExecute)
        {
            return;
        }

        _log?.Invoke(new(
            "Module execution blocked by recovery",
            moduleId,
            readiness.Status,
            readiness.Status.ToString()));
        throw new ModuleExecutionBlockedException(moduleId, readiness.Status);
    }

    private sealed class CompositeLease(IAsyncDisposable first, IAsyncDisposable second) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    private sealed class ConsumerView(ModuleTransactionRecoveryGate owner)
        : IModuleExecutionReadinessGate
    {
        public Task RecoveryInspected => owner._recoveryInspected.Task;

        public ModuleExecutionReadiness GetReadiness(string moduleId) =>
            owner.GetReadiness(moduleId);

        public Task WaitForRecoveryInspectionAsync(CancellationToken cancellationToken = default) =>
            owner.WaitForRecoveryInspectionAsync(cancellationToken);

        public ValueTask<IAsyncDisposable> EnterExecutionAsync(
            string moduleId,
            CancellationToken cancellationToken = default) =>
            owner.EnterExecutionAsync(moduleId, cancellationToken);
    }

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
