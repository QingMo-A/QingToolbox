using System.Windows;
using QingToolbox.Core.Settings;
using QingToolbox.Shell.Services;

namespace QingToolbox.Shell.Startup;

public enum StartupSessionState { Starting, Discovering, Presenting, RestoringModules, Ready, Exiting }

public sealed class StartupSessionCoordinator(ApplicationLaunchOptions launchOptions) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _manualActivationRequested;
    private MainWindow? _mainWindow;
    private FloatingBadgeManager? _badgeManager;

    public StartupSessionState State { get; private set; } = StartupSessionState.Starting;
    public bool ManualActivationRequested => Volatile.Read(ref _manualActivationRequested) != 0;
    public CancellationToken LifetimeToken => _lifetime.Token;

    public void Attach(MainWindow mainWindow, FloatingBadgeManager badgeManager)
    {
        _mainWindow = mainWindow;
        _badgeManager = badgeManager;
    }

    public void MarkManualActivationRequested() => Interlocked.Exchange(ref _manualActivationRequested, 1);
    public StartupPresentationMode GetEffectivePresentation(StartupPresentationMode configuredMode) =>
        !launchOptions.IsStartupLaunch || ManualActivationRequested
            ? StartupPresentationMode.MainWindow
            : configuredMode;
    public void BeginDiscovery() => State = StartupSessionState.Discovering;
    public void BeginModuleRestore() => State = StartupSessionState.RestoringModules;
    public void Complete() => State = StartupSessionState.Ready;

    public async Task PresentAsync(StartupPresentationMode configuredMode)
    {
        State = StartupSessionState.Presenting;
        if (_mainWindow is null || _badgeManager is null) return;
        var effectiveMode = GetEffectivePresentation(configuredMode);
        if (effectiveMode == StartupPresentationMode.MainWindow)
        {
            await ActivateMainWindowAsync();
            return;
        }

        _mainWindow.ShowActivated = true;
        _mainWindow.ShowInTaskbar = true;
        switch (effectiveMode)
        {
            case StartupPresentationMode.Minimized:
                _mainWindow.Opacity = 1;
                _mainWindow.Show();
                _mainWindow.WindowState = WindowState.Minimized;
                break;
            case StartupPresentationMode.FloatingBadge:
                try
                {
                    await _badgeManager.EnterAsync(_lifetime.Token);
                    _mainWindow.Opacity = 1;
                }
                catch { await ActivateMainWindowAsync(); }
                break;
            default:
                await ActivateMainWindowAsync();
                break;
        }

        if (ManualActivationRequested) await ActivateMainWindowAsync();
    }

    public async Task ActivateMainWindowAsync()
    {
        MarkManualActivationRequested();
        if (_mainWindow is null || _badgeManager is null) return;
        await _badgeManager.RestoreAsync(_lifetime.Token);
        _mainWindow.Opacity = 1;
        _mainWindow.ShowActivated = true;
        _mainWindow.ShowInTaskbar = true;
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.Activate();
        _mainWindow.Focus();
    }

    public void PrepareForExit()
    {
        State = StartupSessionState.Exiting;
        _lifetime.Cancel();
    }

    public void Dispose() { PrepareForExit(); _lifetime.Dispose(); }
}
