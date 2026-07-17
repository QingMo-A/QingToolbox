using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using QingToolbox.Shell.ViewModels;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;
using QingToolbox.Core.Settings;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Shell.Views;
using QingToolbox.Shell.Windowing;

namespace QingToolbox.Shell;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly FloatingBadgeManager _floatingBadgeManager;
    private readonly StartupSessionCoordinator _startupSession;
    private readonly UserSettingsService _settingsService;
    private readonly ILocalizationService _localization;
    private readonly INotificationAreaIcon _notificationArea;
    private readonly ApplicationExitCoordinator _exitCoordinator;
    private readonly ModuleWindowManager _moduleWindowManager;
    private readonly StartupPreferenceReader _startupPreferenceReader;
    private readonly ApplicationPaths _applicationPaths;
    private readonly ApplicationLaunchOptions _launchOptions;
    private readonly StartupHealthJournal _startupJournal;
    private int _closeRequestPending;
    private WindowState _notificationAreaRestoreState = WindowState.Normal;

    public MainWindow(
        MainWindowViewModel viewModel,
        FloatingBadgeManager floatingBadgeManager,
        StartupSessionCoordinator startupSession,
        UserSettingsService settingsService,
        ILocalizationService localization,
        INotificationAreaIcon notificationArea,
        ApplicationExitCoordinator exitCoordinator,
        ModuleWindowManager moduleWindowManager,
        StartupPreferenceReader startupPreferenceReader,
        ApplicationPaths applicationPaths,
        ApplicationLaunchOptions launchOptions,
        StartupHealthJournal startupJournal)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _floatingBadgeManager = floatingBadgeManager;
        _startupSession = startupSession;
        _settingsService = settingsService;
        _localization = localization;
        _notificationArea = notificationArea;
        _exitCoordinator = exitCoordinator;
        _moduleWindowManager = moduleWindowManager;
        _startupPreferenceReader = startupPreferenceReader;
        _applicationPaths = applicationPaths;
        _launchOptions = launchOptions;
        _startupJournal = startupJournal;
        _startupSession.Attach(this, floatingBadgeManager);
        _floatingBadgeManager.Attach(this);
        DataContext = viewModel;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
    }

    private async void OnFloatingBadgeClick(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        button.IsEnabled = false;
        try { await _floatingBadgeManager.EnterAsync(); }
        catch { _viewModel.StatusMessage = _viewModel.Strings["floatingBadge.restoreFailed"]; }
        finally { button.IsEnabled = true; }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.IsCompactWindow = e.NewSize.Width < 720;
        if (_viewModel.IsCompactWindow)
        {
            _viewModel.IsSidebarExpanded = false;
            AnimateSidebarWidth(76, TimeSpan.FromMilliseconds(180));
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try { await PresentCriticalStartupAsync(); _ = InitializeApplicationInBackgroundAsync(); }
        catch (OperationCanceledException) when (_startupSession.State == StartupSessionState.Exiting)
        {
            // Application shutdown cancellation is an expected lifecycle outcome.
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Shell startup failed: {exception.GetType().Name}");
            if (_startupSession.State != StartupSessionState.Exiting)
            {
                Opacity = 1;
                ShowActivated = true;
                ShowInTaskbar = true;
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Show();
                Activate();
                _viewModel.StatusMessage = _viewModel.Strings["status.startupFailed"];
            }
        }
    }

    private async Task PresentCriticalStartupAsync()
    {
        var preferences = await _startupPreferenceReader.ReadAsync(
            _applicationPaths.SettingsPath, _launchOptions.IsStartupLaunch, _startupSession.LifetimeToken);
        _viewModel.ApplyStartupPreferences(preferences);
        await _startupSession.PresentAsync(preferences.PresentationMode);
        _startupJournal.Mark(StartupPhase.PresentationReady);
    }

    private async Task InitializeApplicationInBackgroundAsync()
    {
        try
        {
        _startupSession.BeginDiscovery();
        await _viewModel.InitializeDiscoveryAsync(_startupSession.LifetimeToken);
        _startupJournal.Mark(StartupPhase.ModuleDiscoveryComplete);
        _startupSession.BeginModuleRestore();
        await _viewModel.RestoreAuthorizedStartupModulesAsync(_startupSession.LifetimeToken);
        _startupJournal.Mark(StartupPhase.AuthorizedModulesRestored);
        if (_startupSession.State != StartupSessionState.Exiting) { _startupSession.Complete(); _startupJournal.Mark(StartupPhase.Ready); }
        }
        catch(OperationCanceledException) when(_startupSession.State==StartupSessionState.Exiting){}
        catch(Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Background startup failed: {exception.GetType().Name}");
            _viewModel.StatusMessage = _viewModel.Strings["status.startupFailed"];
        }
    }

    private void OnSidebarMouseEnter(object sender, MouseEventArgs e)
    {
        if (_viewModel.IsCompactWindow) return;
        _viewModel.IsSidebarExpanded = true;
        AnimateSidebarWidth(236, TimeSpan.FromMilliseconds(380));
    }

    private void OnSidebarMouseLeave(object sender, MouseEventArgs e)
    {
        if (_viewModel.IsSidebarPinned)
        {
            return;
        }

        _viewModel.IsSidebarExpanded = false;
        AnimateSidebarWidth(76, TimeSpan.FromMilliseconds(340));
    }

    private void OnPinSidebarClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsCompactWindow) return;
        _viewModel.ToggleSidebarPinCommand.Execute(null);
        _viewModel.IsSidebarExpanded = _viewModel.IsSidebarPinned || Sidebar.IsMouseOver;
        AnimateSidebarWidth(
            _viewModel.IsSidebarExpanded ? 236 : 76,
            TimeSpan.FromMilliseconds(_viewModel.IsSidebarExpanded ? 380 : 340));
    }

    private void AnimateSidebarWidth(double targetWidth, TimeSpan duration)
    {
        Sidebar.BeginAnimation(WidthProperty, null);
        var animation = new DoubleAnimation
        {
            From = Sidebar.ActualWidth,
            To = targetWidth,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        Sidebar.BeginAnimation(WidthProperty, animation);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitCoordinator.ApplicationExitRequested) return;
        e.Cancel = true;
        if (Interlocked.Exchange(ref _closeRequestPending, 1) != 0) return;
        _ = HandleMainWindowCloseRequestObservedAsync();
    }

    private async Task HandleMainWindowCloseRequestObservedAsync()
    {
        try { await HandleMainWindowCloseRequestAsync(); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Main window close request failed: {exception.GetType().Name}");
            EnsureMainWindowVisible();
            _viewModel.StatusMessage = _localization.GetString("closeBehavior.saveFailed");
        }
        finally { Interlocked.Exchange(ref _closeRequestPending, 0); }
    }

    internal async Task HandleMainWindowCloseRequestAsync()
    {
        if (_exitCoordinator.ApplicationExitRequested || _startupSession.State == StartupSessionState.Exiting) return;
        var behavior = (await _settingsService.ReadAsync()).MainWindowCloseBehavior;
        if (behavior == MainWindowCloseBehavior.Ask)
        {
            var dialog = new CloseBehaviorDialog(_localization) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedBehavior is not { } selected)
            {
                EnsureMainWindowVisible();
                return;
            }
            if (selected == MainWindowCloseBehavior.MinimizeToNotificationArea && !_notificationArea.Initialize())
            {
                EnsureMainWindowVisible();
                _viewModel.StatusMessage = _localization.GetString("notificationArea.unavailable");
                return;
            }
            try
            {
                await _settingsService.UpdateAsync(settings => settings.MainWindowCloseBehavior = selected);
                _viewModel.SetCloseBehaviorFromCloseDialog(selected);
            }
            catch
            {
                EnsureMainWindowVisible();
                _viewModel.StatusMessage = _localization.GetString("closeBehavior.saveFailed");
                return;
            }
            behavior = selected;
        }

        if (behavior == MainWindowCloseBehavior.MinimizeToNotificationArea)
            await MinimizeMainWindowToNotificationAreaAsync();
        else if (behavior == MainWindowCloseBehavior.ExitApplication)
            await _exitCoordinator.RequestExitAsync(ApplicationExitReason.UserRequested);
    }

    internal Task MinimizeMainWindowToNotificationAreaAsync()
    {
        if (!_notificationArea.Initialize())
        {
            EnsureMainWindowVisible();
            _viewModel.StatusMessage = _localization.GetString("notificationArea.unavailable");
            return Task.CompletedTask;
        }
        _notificationAreaRestoreState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        _moduleWindowManager.SuspendForFloatingBadge();
        ShowInTaskbar = false;
        Hide();
        return Task.CompletedTask;
    }

    internal async Task RestoreMainWindowAsync()
    {
        if (_exitCoordinator.ApplicationExitRequested) return;
        await _floatingBadgeManager.RestoreAsync();
        if (_exitCoordinator.ApplicationExitRequested) return;
        Opacity = 1;
        ShowActivated = true;
        ShowInTaskbar = true;
        Show();
        WindowState = _notificationAreaRestoreState;
        _moduleWindowManager.RestoreAfterFloatingBadge();
        Activate();
        Focus();
    }

    internal async Task SwitchToFloatingBadgeFromNotificationAreaAsync()
    {
        if (_exitCoordinator.ApplicationExitRequested) return;
        try
        {
            await _floatingBadgeManager.EnterFromNotificationAreaAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Notification area badge transition failed: {exception.GetType().Name}");
            EnsureMainWindowVisible();
            _moduleWindowManager.RestoreAfterFloatingBadge();
            _viewModel.StatusMessage = _localization.GetString("floatingBadge.restoreFailed");
        }
    }

    internal bool HasRecoverySurface =>
        _exitCoordinator.ApplicationExitRequested || IsVisible || _floatingBadgeManager.State == FloatingBadgeState.Badge ||
        _notificationArea.IsAvailable || (ShowInTaskbar && WindowState == WindowState.Minimized);

    private void EnsureMainWindowVisible()
    {
        Opacity = 1;
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
