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
    NotificationAreaService notificationArea,
    FloatingBadgeManager floatingBadgeManager,
    ModuleWindowManager moduleWindowManager,
    ModuleRuntimeManager runtimeManager)
{
    private readonly object _sync = new();
    private Task? _exitTask;
    private Func<Task>? _stopActivation;

    public bool ApplicationExitRequested { get; private set; }
    public void ConfigureStopActivation(Func<Task> stopActivation) => _stopActivation = stopActivation;

    public Task RequestExitAsync(ApplicationExitReason reason)
    {
        lock (_sync)
        {
            ApplicationExitRequested = true;
            return _exitTask ??= Application.Current.Dispatcher.InvokeAsync(() => ExitCoreAsync(reason)).Task.Unwrap();
        }
    }

    private async Task ExitCoreAsync(ApplicationExitReason reason)
    {
        startupSession.PrepareForExit();
        notificationArea.PrepareForExit();
        floatingBadgeManager.PrepareForApplicationExit();
        if (_stopActivation is not null)
        {
            try { await _stopActivation(); }
            catch (Exception exception) { Debug.WriteLine($"Could not stop instance activation: {exception.GetType().Name}"); }
        }
        moduleWindowManager.CloseAll();
        try { await runtimeManager.DisposeAsync(); }
        catch (Exception exception) { Debug.WriteLine($"Failed to unload modules during {reason}: {exception}"); }
        if (Application.Current.MainWindow is { } mainWindow) mainWindow.Close();
        Application.Current.Shutdown();
    }
}
