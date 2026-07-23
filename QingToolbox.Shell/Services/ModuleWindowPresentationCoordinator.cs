namespace QingToolbox.Shell.Services;

public sealed class ModuleWindowPresentationCoordinator
{
    private readonly Action _suspendInProcess;
    private readonly Action _restoreInProcess;
    private readonly Func<CancellationToken, Task<bool>> _suspendWorkers;
    private readonly Func<CancellationToken, Task<bool>> _restoreWorkers;
    private readonly Action<string, string> _logFailure;

    public ModuleWindowPresentationCoordinator(
        ModuleWindowManager moduleWindowManager,
        ModuleProcessBroker moduleProcessBroker,
        SessionLogService log)
        : this(
            moduleWindowManager.SuspendForFloatingBadge,
            moduleWindowManager.RestoreAfterFloatingBadge,
            moduleProcessBroker.SuspendWindowsAsync,
            moduleProcessBroker.RestoreWindowsAsync,
            (operation, failure) => log.Warning("ModulePresentation",
                $"Module window transition failed; operation={operation}; failure={failure}."))
    {
    }

    internal ModuleWindowPresentationCoordinator(
        Action suspendInProcess,
        Action restoreInProcess,
        Func<CancellationToken, Task<bool>> suspendWorkers,
        Func<CancellationToken, Task<bool>> restoreWorkers,
        Action<string, string>? logFailure = null)
    {
        _suspendInProcess = suspendInProcess;
        _restoreInProcess = restoreInProcess;
        _suspendWorkers = suspendWorkers;
        _restoreWorkers = restoreWorkers;
        _logFailure = logFailure ?? ((_, _) => { });
    }

    public async Task<bool> SuspendAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _suspendInProcess();
        }
        catch (Exception exception)
        {
            _logFailure("SuspendInProcess", exception.GetType().Name);
            try { _restoreInProcess(); }
            catch (Exception restoreException)
            {
                _logFailure("SuspendInProcessCompensation", restoreException.GetType().Name);
            }
            return false;
        }
        try
        {
            if (await _suspendWorkers(cancellationToken)) return true;
            _logFailure("Suspend", "ModuleHost.BatchCommandFailed");
        }
        catch (Exception exception)
        {
            _logFailure("Suspend", exception.GetType().Name);
        }

        // Compensation is best-effort but exhaustive. The main window caller must
        // remain visible even when one of these restores also fails.
        try
        {
            if (!await _restoreWorkers(CancellationToken.None))
                _logFailure("SuspendCompensation", "ModuleHost.BatchRestoreFailed");
        }
        catch (Exception exception)
        {
            _logFailure("SuspendCompensation", exception.GetType().Name);
        }
        finally
        {
            try { _restoreInProcess(); }
            catch (Exception exception)
            {
                _logFailure("SuspendInProcessCompensation", exception.GetType().Name);
            }
        }
        return false;
    }

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var inProcessRestored = true;
        try { _restoreInProcess(); }
        catch (Exception exception)
        {
            inProcessRestored = false;
            _logFailure("RestoreInProcess", exception.GetType().Name);
        }
        try
        {
            var restored = await _restoreWorkers(cancellationToken);
            if (!restored) _logFailure("Restore", "ModuleHost.BatchCommandFailed");
            return inProcessRestored && restored;
        }
        catch (Exception exception)
        {
            _logFailure("Restore", exception.GetType().Name);
            return false;
        }
    }
}
