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
    private BadgePlacementSnapshot? _lastBadgePlacement;
    private Task _pendingBadgePositionSave = Task.CompletedTask;
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
            var badge = _badgeWindow;
            BadgePlacementSnapshot? badgePlacement = null;
            try
            {
                if (badge is not null)
                {
                    try { badgePlacement = ConstrainAndCaptureBadgePlacement(badge); }
                    catch (Exception exception)
                    {
                        Debug.WriteLine($"Could not capture badge position while restoring: {exception.GetType().Name}");
                    }
                    if (badgePlacement is not null) _lastBadgePlacement = badgePlacement;
                }
                // Remove the floating surface immediately. Restoring module windows and
                // persisting the badge position may take longer than showing the Shell.
                if (badge?.IsVisible == true) badge.Hide();
                var snapshot = _snapshot ?? WindowSnapshot.Capture(mainWindow);
                mainWindow.ShowInTaskbar = snapshot.ShowInTaskbar;
                mainWindow.Show();
                RestoreMainWindow(mainWindow, snapshot);
                if (!await moduleWindowPresentation.RestoreAsync(cancellationToken))
                    throw new InvalidOperationException("One or more module worker windows could not be restored.");
                mainWindow.Activate();
                mainWindow.Focus();

                if (badge is not null)
                {
                    try
                    {
                        await AwaitPendingBadgePositionSaveAsync().ConfigureAwait(true);
                        if (badgePlacement is not null)
                            await SaveBadgePositionAsync(badgePlacement, cancellationToken).ConfigureAwait(true);
                    }
                    catch (Exception exception)
                    {
                        Debug.WriteLine($"Could not save badge position while restoring: {exception.GetType().Name}");
                    }
                    CloseBadge(badge);
                    if (ReferenceEquals(_badgeWindow, badge)) _badgeWindow = null;
                }
                if (!_exitRequested) _stateMachine.TryCompleteRestore();
            }
            catch
            {
                if (!_exitRequested)
                {
                    if (ReferenceEquals(_badgeWindow, badge) && badge is { IsVisible: false }) badge.Show();
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
        await PrepareForApplicationExitAsync();
        await CompleteExitAfterTransitionAsync();
    }

    public async Task PrepareForApplicationExitAsync()
    {
        PrepareForApplicationExit();
        await PersistActiveBadgePositionBestEffortAsync().ConfigureAwait(true);
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
            var badge = _badgeWindow;
            if (badge is not null)
            {
                await PersistActiveBadgePositionBestEffortAsync().ConfigureAwait(true);
                CloseBadge(badge);
                if (ReferenceEquals(_badgeWindow, badge)) _badgeWindow = null;
            }
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
        badge.DragCompletedAsync += () => SaveBadgePositionAfterDragAsync(badge);
        badge.Closed += (_, _) => OnBadgeClosed(badge);
        _badgeWindow = badge;
        return badge;
    }

    private Task SaveBadgePositionAfterDragAsync(FloatingBadgeWindow badge)
    {
        try
        {
            // DragMove has returned at this point, so capture the committed
            // HWND placement rather than WPF's potentially stale Left/Top.
            var placement = ConstrainAndCaptureBadgePlacement(badge);
            _lastBadgePlacement = placement;
            return QueueBadgePositionSave(placement);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not save badge position after drag: {exception.GetType().Name}");
            return Task.CompletedTask;
        }
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

    private void PositionVisibleBadge(FloatingBadgeWindow badge, Window mainWindow, UserSettings settings)
    {
        var fallbackPixel = mainWindow.PointToScreen(new Point(mainWindow.ActualWidth / 2, mainWindow.ActualHeight / 2));
        var monitorDeviceName = _lastBadgePlacement?.MonitorDeviceName ?? settings.FloatingBadgeMonitorDeviceName;
        var monitor = FloatingBadgePlacement.ResolveMonitor(monitorDeviceName, fallbackPixel);
        var badgePixels = FloatingBadgePlacement.GetBadgePixelSize(badge, monitor);
        Point requested;
        if (_lastBadgePlacement is { } sessionPlacement)
        {
            requested = FloatingBadgePlacement.PositionFromRatios(
                monitor, badgePixels, sessionPlacement.HorizontalRatio, sessionPlacement.VerticalRatio);
        }
        else if (settings.FloatingBadgeHorizontalRatio is not null && settings.FloatingBadgeVerticalRatio is not null)
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
        _lastBadgePlacement = CaptureBadgePlacement(badge);
    }

    private static BadgePlacementSnapshot ConstrainAndCaptureBadgePlacement(FloatingBadgeWindow badge)
    {
        var topLeft = FloatingBadgePlacement.GetWindowPixelTopLeft(badge);
        var monitor = FloatingBadgePlacement.GetMonitorAt(topLeft);
        FloatingBadgePlacement.SetBadgePixelPosition(badge, monitor, topLeft);
        return CaptureBadgePlacement(badge);
    }

    private static BadgePlacementSnapshot CaptureBadgePlacement(FloatingBadgeWindow badge)
    {
        var topLeft = FloatingBadgePlacement.GetWindowPixelTopLeft(badge);
        var monitor = FloatingBadgePlacement.GetMonitorAt(topLeft);
        var badgePixels = FloatingBadgePlacement.GetBadgePixelSize(badge, monitor);
        var ratios = FloatingBadgePlacement.RatiosFromPosition(monitor, topLeft, badgePixels);
        var persistedDips = FloatingBadgePlacement.PixelPositionToDips(monitor, topLeft);
        return new BadgePlacementSnapshot(
            monitor.DeviceName,
            ratios.Horizontal,
            ratios.Vertical,
            double.IsFinite(persistedDips.X) ? persistedDips.X : null,
            double.IsFinite(persistedDips.Y) ? persistedDips.Y : null);
    }

    private Task QueueBadgePositionSave(BadgePlacementSnapshot placement)
    {
        var previous = _pendingBadgePositionSave;
        _pendingBadgePositionSave = PersistAfterAsync(previous, placement);
        return _pendingBadgePositionSave;
    }

    private async Task PersistAfterAsync(Task previous, BadgePlacementSnapshot placement)
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not complete an earlier badge position save: {exception.GetType().Name}");
        }

        try { await SaveBadgePositionAsync(placement, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not save badge position: {exception.GetType().Name}");
        }
    }

    private async Task AwaitPendingBadgePositionSaveAsync()
    {
        try { await _pendingBadgePositionSave.ConfigureAwait(true); }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not complete pending badge position save: {exception.GetType().Name}");
        }
    }

    private async Task PersistActiveBadgePositionBestEffortAsync()
    {
        var badge = _badgeWindow;
        if (badge is null) return;

        try
        {
            var placement = ConstrainAndCaptureBadgePlacement(badge);
            _lastBadgePlacement = placement;
            await AwaitPendingBadgePositionSaveAsync().ConfigureAwait(true);
            await SaveBadgePositionAsync(placement, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not save badge position during exit: {exception.GetType().Name}");
        }
    }

    private async Task SaveBadgePositionAsync(
        BadgePlacementSnapshot placement,
        CancellationToken cancellationToken = default)
    {
        await settingsService.UpdateAsync(settings =>
        {
            settings.FloatingBadgeLeft = placement.LeftDips;
            settings.FloatingBadgeTop = placement.TopDips;
            settings.HasFloatingBadgePosition = placement.LeftDips is not null && placement.TopDips is not null;
            settings.FloatingBadgeMonitorDeviceName = placement.MonitorDeviceName;
            settings.FloatingBadgeHorizontalRatio = placement.HorizontalRatio;
            settings.FloatingBadgeVerticalRatio = placement.VerticalRatio;
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

    private sealed record BadgePlacementSnapshot(
        string MonitorDeviceName,
        double HorizontalRatio,
        double VerticalRatio,
        double? LeftDips,
        double? TopDips);

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
