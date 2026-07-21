using System.Diagnostics;
using System.Windows;
using QingToolbox.Core.Runtime;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.Services;

public enum ApplicationExitReason
{
    UserRequested,
    NotificationAreaMenu,
    FloatingBadgeMenu,
    SessionEnding,
    ApplicationFailure
}

public sealed class ApplicationExitCoordinator(
    StartupSessionCoordinator startupSession,
    INotificationAreaIcon notificationArea,
    FloatingBadgeManager floatingBadgeManager,
    ModuleWindowManager moduleWindowManager,
    ModuleRuntimeManager runtimeManager,
    ModuleTransactionRecoveryGate maintenanceGate,
    ModuleProcessBroker processBroker)
{
    private readonly object _sync = new();
    private Task? _exitTask;
    private Func<Task>? _stopActivation;

    public bool ApplicationExitRequested { get; private set; }
    public bool RuntimeCleanupCompleted { get; private set; }
    public void ConfigureStopActivation(Func<Task> stopActivation) => _stopActivation = stopActivation;

    public Task RequestExitAsync(ApplicationExitReason reason)
    {
        lock (_sync)
        {
            ApplicationExitRequested = true;
            return _exitTask ??= RunExitAsync(reason);
        }
    }

    private async Task RunExitAsync(ApplicationExitReason reason)
    {
        try
        {
            var dispatcher = Application.Current.Dispatcher;
            if (dispatcher.CheckAccess()) await ExitCoreAsync(reason);
            else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                await dispatcher.InvokeAsync(() => ExitCoreAsync(reason)).Task.Unwrap();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Exit dispatch failed: {exception.GetType().Name}");
            try { Application.Current.Shutdown(); }
            catch (Exception shutdownException)
            {
                Debug.WriteLine($"Exit fallback shutdown failed: {shutdownException.GetType().Name}");
            }
        }
    }

    private async Task ExitCoreAsync(ApplicationExitReason reason)
    {
        await using var maintenance = await maintenanceGate.BeginShutdownAsync(TimeSpan.FromSeconds(8));
        if (maintenance is null)
        {
            Debug.WriteLine("Exit maintenance gate timed out; runtime cleanup skipped fail-closed.");
            Application.Current.Shutdown();
            return;
        }
        var stages = new List<ExitCleanupStage>
        {
            SyncStage("startup session", startupSession.PrepareForExit),
            SyncStage("notification area", notificationArea.PrepareForExit),
            SyncStage("floating badge", floatingBadgeManager.PrepareForApplicationExit),
            new("instance activation", () => _stopActivation?.Invoke() ?? Task.CompletedTask),
            SyncStage("module windows", () =>
            {
                var failed = moduleWindowManager.CloseAllSafely();
                if (failed > 0) Debug.WriteLine($"Exit stage module windows failed: {failed} window(s)");
            }),
            new("module processes", async () => await processBroker.DisposeAsync()),
            new("module runtime", async () =>
            {
                await runtimeManager.DisposeAsync();
                RuntimeCleanupCompleted = true;
            }),
            SyncStage("main window", () => Application.Current.MainWindow?.Close())
        };
        await ExitCleanupPipeline.RunAsync(
            stages,
            () => Application.Current.Shutdown(),
            (stage, exception) => Debug.WriteLine($"Exit stage {stage} failed: {exception.GetType().Name}"));
    }

    private static ExitCleanupStage SyncStage(string stage, Action action)
    {
        return new ExitCleanupStage(stage, () =>
        {
            action();
            return Task.CompletedTask;
        });
    }
}
