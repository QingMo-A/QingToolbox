using System.Diagnostics;
using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Core.Settings;
using QingToolbox.Shell.Views;
using QingToolbox.Shell.Windowing;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.Services;

public sealed class FloatingBadgeManager(
    ModuleWindowPresentationCoordinator moduleWindowPresentation,
    UserSettingsService settingsService,
    ILocalizationService localization,
    ApplicationExecutionEnvironment environment) : IDisposable
{
    private readonly FloatingBadgeStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private MainWindow? _mainWindow;
    private FloatingBadgeWindow? _badgeWindow;
    private WindowSnapshot? _snapshot;
    private bool _exitRequested;
    private Func<Task>? _applicationExitRequest;
    private bool _disposed;

    public FloatingBadgeState State => _stateMachine.State;
    public bool IsTransitioning => State is FloatingBadgeState.EnteringBadge or FloatingBadgeState.Restoring;
    public event EventHandler? StateChanged;

    public void Attach(MainWindow mainWindow) => _mainWindow = mainWindow;
    public void ConfigureApplicationExit(Func<Task> request) => _applicationExitRequest = request;

    public async Task EnterAsync(CancellationToken cancellationToken = default)
        => await EnterCoreAsync(false, cancellationToken);

    public async Task EnterFromNotificationAreaAsync(CancellationToken cancellationToken = default)
        => await EnterCoreAsync(true, cancellationToken);

    private async Task EnterCoreAsync(bool preserveSuspendedWindows, CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || _exitRequested || _mainWindow is null || !_stateMachine.TryBeginEnter()) return;
            RaiseStateChanged();
            var mainWindow = _mainWindow;
            FloatingBadgeWindow? badge = null;
            try
            {
                _snapshot = WindowSnapshot.Capture(mainWindow, preserveSuspendedWindows ? true : null);
                var settings = await settingsService.ReadAsync(cancellationToken);
                badge = CreateBadgeWindow();
                badge.Opacity = 0;
                badge.Show();
                badge.UpdateLayout();
                PositionVisibleBadge(badge, mainWindow, settings);
                badge.Opacity = 1;

                if (_exitRequested) return;
                if (!preserveSuspendedWindows)
                {
                    if (!await moduleWindowPresentation.SuspendAsync(cancellationToken))
                        throw new InvalidOperationException("One or more module worker windows could not be suspended.");
                }
                mainWindow.ShowInTaskbar = false;
                mainWindow.Hide();
                _badgeWindow = badge;
                badge.Activate();
                badge.Focus();
                _stateMachine.TryCompleteEnter();
            }
            catch
            {
                CloseBadge(badge);
                if (!_exitRequested)
                {
                    mainWindow.ShowInTaskbar = _snapshot?.ShowInTaskbar ?? true;
                    mainWindow.Show();
                    await moduleWindowPresentation.RestoreAsync(CancellationToken.None);
                    _stateMachine.TryFailEnter();
                    EnsureRecoverableWindow();
                }
                throw;
            }
            finally
            {
                if (_exitRequested) CloseBadge(badge);
                RaiseStateChanged();
            }
        }
        finally
        {
            _transitionGate.Release();
            if (_exitRequested) await CompleteExitAfterTransitionAsync();
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || _exitRequested || _mainWindow is null || !_stateMachine.TryBeginRestore()) return;
            RaiseStateChanged();
            var mainWindow = _mainWindow;
            try
            {
                var snapshot = _snapshot ?? WindowSnapshot.Capture(mainWindow);
                mainWindow.ShowInTaskbar = snapshot.ShowInTaskbar;
                mainWindow.Show();
                RestoreMainWindow(mainWindow, snapshot);
                if (!await moduleWindowPresentation.RestoreAsync(cancellationToken))
                    throw new InvalidOperationException("One or more module worker windows could not be restored.");
                mainWindow.Activate();
                mainWindow.Focus();

                if (_badgeWindow is { } badge)
                {
                    await SaveBadgePositionAsync(badge, cancellationToken);
                    CloseBadge(badge);
                    _badgeWindow = null;
                }
                if (!_exitRequested) _stateMachine.TryCompleteRestore();
            }
            catch
            {
                if (!_exitRequested)
                {
                    if (_badgeWindow is { IsVisible: false } badge) badge.Show();
                    _stateMachine.TryFailRestore();
                    EnsureRecoverableWindow();
                }
                throw;
            }
            finally { RaiseStateChanged(); }
        }
        finally
        {
            _transitionGate.Release();
            if (_exitRequested) await CompleteExitAfterTransitionAsync();
        }
    }

    public async Task ExitApplicationAsync()
    {
        if (_applicationExitRequest is not null)
        {
            await _applicationExitRequest();
            return;
        }
        PrepareForApplicationExit();
        await CompleteExitAfterTransitionAsync();
    }

    public void PrepareForApplicationExit()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        _stateMachine.TryBeginExit();
        _badgeWindow?.AllowClose();
        RaiseStateChanged();
    }

    public void OnMainWindowClosing() => PrepareForApplicationExit();

    private async Task CompleteExitAfterTransitionAsync()
    {
        await _transitionGate.WaitAsync();
        try
        {
            if (_mainWindow is null) return;
            CloseBadge(_badgeWindow);
            _badgeWindow = null;
            if (!_mainWindow.Dispatcher.HasShutdownStarted) _mainWindow.Close();
        }
        finally { _transitionGate.Release(); }
    }

    private FloatingBadgeWindow CreateBadgeWindow()
    {
        if (_badgeWindow is not null) return _badgeWindow;
        var badge = new FloatingBadgeWindow(localization, environment.DisplayName);
        badge.RestoreRequested += async (_, _) => await RestoreSafelyAsync();
        badge.ExitRequested += async (_, _) => await ExitSafelyAsync();
        badge.DragCompleted += async (_, _) =>
        {
            try
            {
                ConstrainBadgeToCurrentMonitor(badge);
                await SaveBadgePositionAsync(badge);
            }
            catch (Exception exception) { Debug.WriteLine($"Could not save badge position: {exception.GetType().Name}"); }
        };
        badge.Closed += (_, _) => OnBadgeClosed(badge);
        _badgeWindow = badge;
        return badge;
    }

    private async Task RestoreSafelyAsync()
    {
        try { await RestoreAsync(); }
        catch (Exception exception) { Debug.WriteLine($"Could not restore the main window: {exception.GetType().Name}"); }
    }

    private async Task ExitSafelyAsync()
    {
        try { await ExitApplicationAsync(); }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not exit from the floating badge: {exception.GetType().Name}");
            if (!_exitRequested) EnsureRecoverableWindow();
        }
    }

    private void OnBadgeClosed(FloatingBadgeWindow badge)
    {
        if (!ReferenceEquals(_badgeWindow, badge)) return;
        _badgeWindow = null;
        if (!_exitRequested && _mainWindow is { IsVisible: false }) _ = RestoreSafelyAsync();
    }

    private static void PositionVisibleBadge(FloatingBadgeWindow badge, Window mainWindow, UserSettings settings)
    {
        var fallbackPixel = mainWindow.PointToScreen(new Point(mainWindow.ActualWidth / 2, mainWindow.ActualHeight / 2));
        var monitor = FloatingBadgePlacement.ResolveMonitor(settings.FloatingBadgeMonitorDeviceName, fallbackPixel);
        var badgePixels = FloatingBadgePlacement.GetBadgePixelSize(badge, monitor);
        Point requested;
        if (settings.FloatingBadgeHorizontalRatio is not null && settings.FloatingBadgeVerticalRatio is not null)
        {
            requested = FloatingBadgePlacement.PositionFromRatios(
                monitor, badgePixels, settings.FloatingBadgeHorizontalRatio, settings.FloatingBadgeVerticalRatio);
        }
        else if (settings.HasFloatingBadgePosition && settings.FloatingBadgeLeft is { } left &&
                 settings.FloatingBadgeTop is { } top && double.IsFinite(left) && double.IsFinite(top))
        {
            requested = new Point(left * monitor.ScaleX, top * monitor.ScaleY);
        }
        else
        {
            requested = FloatingBadgePlacement.PositionFromRatios(monitor, badgePixels, 1, 0);
        }
        FloatingBadgePlacement.SetBadgePixelPosition(badge, monitor, requested);
    }

    private static void ConstrainBadgeToCurrentMonitor(FloatingBadgeWindow badge)
    {
        var topLeft = FloatingBadgePlacement.GetWindowPixelTopLeft(badge);
        var monitor = FloatingBadgePlacement.GetMonitorAt(topLeft);
        FloatingBadgePlacement.SetBadgePixelPosition(badge, monitor, topLeft);
    }

    private async Task SaveBadgePositionAsync(FloatingBadgeWindow badge, CancellationToken cancellationToken = default)
    {
        var topLeft = FloatingBadgePlacement.GetWindowPixelTopLeft(badge);
        var monitor = FloatingBadgePlacement.GetMonitorAt(topLeft);
        var badgePixels = FloatingBadgePlacement.GetBadgePixelSize(badge, monitor);
        var ratios = FloatingBadgePlacement.RatiosFromPosition(monitor, topLeft, badgePixels);
        await settingsService.UpdateAsync(settings =>
        {
            settings.FloatingBadgeLeft = badge.Left;
            settings.FloatingBadgeTop = badge.Top;
            settings.HasFloatingBadgePosition = double.IsFinite(badge.Left) && double.IsFinite(badge.Top);
            settings.FloatingBadgeMonitorDeviceName = monitor.DeviceName;
            settings.FloatingBadgeHorizontalRatio = ratios.Horizontal;
            settings.FloatingBadgeVerticalRatio = ratios.Vertical;
        }, cancellationToken);
    }

    private static void RestoreMainWindow(MainWindow window, WindowSnapshot snapshot)
    {
        window.WindowState = WindowState.Normal;
        var centerPixel = window.PointToScreen(new Point(window.ActualWidth / 2, window.ActualHeight / 2));
        var monitor = FloatingBadgePlacement.GetMonitorAt(centerPixel);
        var workArea = FloatingBadgePlacement.MonitorWorkAreaToWindowDips(monitor, window);
        var bounds = FloatingBadgePlacement.ConstrainWindowBounds(
            snapshot.Bounds, workArea, new Size(window.MinWidth, window.MinHeight));
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        if (snapshot.State == WindowState.Maximized) window.WindowState = WindowState.Maximized;
    }

    private void EnsureRecoverableWindow()
    {
        if (_exitRequested || _mainWindow is null || _mainWindow.IsVisible || _badgeWindow?.IsVisible == true) return;
        _mainWindow.ShowInTaskbar = _snapshot?.ShowInTaskbar ?? true;
        _mainWindow.Show();
    }

    private static void CloseBadge(FloatingBadgeWindow? badge)
    {
        if (badge is null) return;
        badge.AllowClose();
        badge.Close();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _disposed = true;
        PrepareForApplicationExit();
    }

    private sealed record WindowSnapshot(WindowState State, Rect Bounds, bool ShowInTaskbar)
    {
        internal static WindowSnapshot Capture(Window window, bool? showInTaskbarOverride = null)
        {
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;
            var state = window.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            return new WindowSnapshot(state, bounds, showInTaskbarOverride ?? window.ShowInTaskbar);
        }
    }
}
