using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Core.Localization;
using QingToolbox.Shell.Views;
using QingToolbox.Shell.Windowing;

namespace QingToolbox.Shell.Services;

public sealed class FloatingBadgeManager(
    ModuleWindowManager moduleWindowManager,
    UserSettingsService settingsService,
    ILocalizationService localization) : IDisposable
{
    private readonly FloatingBadgeStateMachine _stateMachine = new();
    private MainWindow? _mainWindow;
    private FloatingBadgeWindow? _badgeWindow;
    private WindowSnapshot? _snapshot;

    public FloatingBadgeState State => _stateMachine.State;
    public bool IsTransitioning => State is FloatingBadgeState.EnteringBadge or FloatingBadgeState.Restoring;
    public event EventHandler? StateChanged;

    public void Attach(MainWindow mainWindow) => _mainWindow = mainWindow;

    public async Task EnterAsync()
    {
        if (_mainWindow is null || !_stateMachine.TryBeginEnter()) return;
        RaiseStateChanged();
        var mainWindow = _mainWindow;
        FloatingBadgeWindow? badge = null;
        try
        {
            _snapshot = WindowSnapshot.Capture(mainWindow);
            badge = CreateBadgeWindow();
            PositionBadgeBeforeShow(badge, mainWindow, await settingsService.LoadAsync());
            badge.Opacity = 0;
            badge.Show();
            badge.UpdateLayout();
            ConstrainBadgeToCurrentMonitor(badge);
            badge.Opacity = 1;

            moduleWindowManager.SuspendForFloatingBadge();
            mainWindow.ShowInTaskbar = false;
            mainWindow.Hide();
            _badgeWindow = badge;
            badge.Activate();
            badge.Focus();
            _stateMachine.CompleteEnter();
        }
        catch
        {
            if (badge is not null)
            {
                badge.AllowClose();
                badge.Close();
            }
            mainWindow.ShowInTaskbar = _snapshot?.ShowInTaskbar ?? true;
            mainWindow.Show();
            moduleWindowManager.RestoreAfterFloatingBadge();
            _stateMachine.FailEnter();
            throw;
        }
        finally
        {
            RaiseStateChanged();
        }
    }

    public async Task RestoreAsync()
    {
        if (_mainWindow is null || !_stateMachine.TryBeginRestore()) return;
        RaiseStateChanged();
        var mainWindow = _mainWindow;
        try
        {
            var snapshot = _snapshot ?? WindowSnapshot.Capture(mainWindow);
            mainWindow.ShowInTaskbar = snapshot.ShowInTaskbar;
            mainWindow.Show();
            RestoreMainWindow(mainWindow, snapshot);
            moduleWindowManager.RestoreAfterFloatingBadge();
            mainWindow.Activate();
            mainWindow.Focus();

            if (_badgeWindow is { } badge)
            {
                await SaveBadgePositionAsync(badge);
                badge.AllowClose();
                badge.Close();
                _badgeWindow = null;
            }
            _stateMachine.CompleteRestore();
        }
        catch
        {
            if (_badgeWindow is { } badge && !badge.IsVisible) badge.Show();
            _stateMachine.FailRestore();
            throw;
        }
        finally
        {
            RaiseStateChanged();
        }
    }

    public void ExitApplication()
    {
        if (_mainWindow is null || !_stateMachine.TryBeginExit()) return;
        RaiseStateChanged();
        if (_badgeWindow is { } badge)
        {
            badge.AllowClose();
            badge.Close();
            _badgeWindow = null;
        }
        _mainWindow.ShowInTaskbar = _snapshot?.ShowInTaskbar ?? true;
        _mainWindow.Show();
        _mainWindow.Close();
    }

    public void OnMainWindowClosing()
    {
        _stateMachine.TryBeginExit();
        if (_badgeWindow is not { } badge) return;
        badge.AllowClose();
        badge.Close();
        _badgeWindow = null;
    }

    private FloatingBadgeWindow CreateBadgeWindow()
    {
        if (_badgeWindow is not null) return _badgeWindow;
        var badge = new FloatingBadgeWindow(localization);
        badge.RestoreRequested += async (_, _) => await RestoreSafelyAsync();
        badge.ExitRequested += (_, _) => ExitApplication();
        badge.DragCompleted += async (_, _) =>
        {
            ConstrainBadgeToCurrentMonitor(badge);
            await SaveBadgePositionAsync(badge);
        };
        return badge;
    }

    private async Task RestoreSafelyAsync()
    {
        try { await RestoreAsync(); }
        catch { /* Keep the badge alive so the application remains recoverable. */ }
    }

    private static void PositionBadgeBeforeShow(
        FloatingBadgeWindow badge, Window mainWindow, LanguageSettings settings)
    {
        var center = mainWindow.PointToScreen(
            new Point(mainWindow.ActualWidth / 2, mainWindow.ActualHeight / 2));
        var workArea = FloatingBadgePlacement.GetWorkAreaInDips(center);
        var requested = settings.HasFloatingBadgePosition &&
                        settings.FloatingBadgeLeft is { } left && double.IsFinite(left) &&
                        settings.FloatingBadgeTop is { } top && double.IsFinite(top)
            ? new Point(left, top)
            : FloatingBadgePlacement.Initial(workArea, new Size(badge.Width, badge.Height));
        var position = FloatingBadgePlacement.Constrain(
            requested, workArea, new Size(badge.Width, badge.Height));
        badge.Left = position.X;
        badge.Top = position.Y;
    }

    private static void ConstrainBadgeToCurrentMonitor(FloatingBadgeWindow badge)
    {
        var center = badge.PointToScreen(new Point(badge.ActualWidth / 2, badge.ActualHeight / 2));
        var workArea = FloatingBadgePlacement.GetWorkAreaInDips(center);
        var position = FloatingBadgePlacement.Constrain(
            new Point(badge.Left, badge.Top), workArea,
            new Size(badge.ActualWidth, badge.ActualHeight));
        badge.Left = position.X;
        badge.Top = position.Y;
    }

    private async Task SaveBadgePositionAsync(FloatingBadgeWindow badge)
    {
        if (!double.IsFinite(badge.Left) || !double.IsFinite(badge.Top)) return;
        try
        {
            var settings = await settingsService.LoadAsync();
            settings.FloatingBadgeLeft = badge.Left;
            settings.FloatingBadgeTop = badge.Top;
            settings.HasFloatingBadgePosition = true;
            await settingsService.SaveAsync(settings);
        }
        catch
        {
        }
    }

    private static void RestoreMainWindow(MainWindow window, WindowSnapshot snapshot)
    {
        window.WindowState = WindowState.Normal;
        var workArea = FloatingBadgePlacement.GetWorkAreaInDips(
            window.PointToScreen(new Point(window.ActualWidth / 2, window.ActualHeight / 2)));
        var position = FloatingBadgePlacement.Constrain(
            new Point(snapshot.Bounds.Left, snapshot.Bounds.Top), workArea,
            snapshot.Bounds.Size);
        window.Left = position.X;
        window.Top = position.Y;
        window.Width = Math.Max(window.MinWidth, snapshot.Bounds.Width);
        window.Height = Math.Max(window.MinHeight, snapshot.Bounds.Height);
        if (snapshot.State == WindowState.Maximized) window.WindowState = WindowState.Maximized;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() => OnMainWindowClosing();

    private sealed record WindowSnapshot(WindowState State, Rect Bounds, bool ShowInTaskbar)
    {
        internal static WindowSnapshot Capture(Window window)
        {
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;
            var state = window.WindowState == WindowState.Maximized
                ? WindowState.Maximized
                : WindowState.Normal;
            return new WindowSnapshot(state, bounds, window.ShowInTaskbar);
        }
    }
}
