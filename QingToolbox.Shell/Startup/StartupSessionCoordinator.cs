using System.Windows;
using QingToolbox.Core.Settings;
using QingToolbox.Shell.Services;

namespace QingToolbox.Shell.Startup;

public enum StartupSessionState { Starting, Discovering, Presenting, RestoringModules, Ready, Exiting }

public sealed class StartupSessionCoordinator(ApplicationLaunchOptions launchOptions)
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();
    private int _manualActivationRequested;
    private int _disposed;
    private readonly Queue<Guid> _pendingStartupTests = new();
    private MainWindow? _mainWindow;
    private FloatingBadgeManager? _badgeManager;

    public StartupSessionState State { get; private set; } = StartupSessionState.Starting;
    public bool ManualActivationRequested => Volatile.Read(ref _manualActivationRequested) != 0;
    public Guid? StartupTestId => launchOptions.StartupTestId;
    public CancellationToken LifetimeToken => _lifetime.Token;

    public void Attach(MainWindow mainWindow, FloatingBadgeManager badgeManager)
    {
        _mainWindow = mainWindow;
        _badgeManager = badgeManager;
    }

    public bool TryRequestManualActivation()
    {
        lock (_stateLock)
        {
            if (State == StartupSessionState.Exiting || _lifetime.IsCancellationRequested) return false;
            Interlocked.Exchange(ref _manualActivationRequested, 1);
            return true;
        }
    }
    public void RecordStartupProbe(Guid? testId)
    {
        if (testId is null) return;
        lock (_stateLock)
        {
            if (State == StartupSessionState.Exiting) return;
            while (_pendingStartupTests.Count >= 16) _pendingStartupTests.Dequeue();
            _pendingStartupTests.Enqueue(testId.Value);
        }
    }

    public IReadOnlyList<Guid> DrainStartupProbes()
    {
        lock (_stateLock)
        {
            var values = _pendingStartupTests.ToArray();
            _pendingStartupTests.Clear();
            return values;
        }
    }
    public StartupPresentationMode GetEffectivePresentation(StartupPresentationMode configuredMode) =>
        !launchOptions.IsStartupLaunch || ManualActivationRequested
            ? StartupPresentationMode.MainWindow
            : configuredMode;
    public void BeginDiscovery() => TrySetState(StartupSessionState.Discovering);
    public void BeginModuleRestore() => TrySetState(StartupSessionState.RestoringModules);
    public void Complete() => TrySetState(StartupSessionState.Ready);

    private bool TrySetState(StartupSessionState state)
    {
        lock (_stateLock)
        {
            if (State == StartupSessionState.Exiting) return false;
            State = state;
            return true;
        }
    }

    public async Task PresentAsync(StartupPresentationMode configuredMode)
    {
        if (!TrySetState(StartupSessionState.Presenting)) return;
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
        if (!TryRequestManualActivation() || _lifetime.IsCancellationRequested ||
            _mainWindow is null || _badgeManager is null) return;
        await _mainWindow.RestoreMainWindowAsync();
    }

    public void PrepareForExit()
    {
        lock (_stateLock)
        {
            if (State == StartupSessionState.Exiting) return;
            State = StartupSessionState.Exiting;
            _lifetime.Cancel();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        PrepareForExit();
        _lifetime.Dispose();
    }
}
