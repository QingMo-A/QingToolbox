using System.ComponentModel;
using System.IO;
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
    private readonly ModuleProcessBroker _moduleProcessBroker;
    private readonly StartupPreferenceSnapshot _startupPreferences;
    private readonly StartupHealthJournal _startupJournal;
    private readonly StartupPipelineCoordinator _startupPipeline;
    private readonly ModuleTransactionRecoveryCoordinator _moduleRecovery;
    private readonly SessionLogService _sessionLog;
    private Task? _backgroundStartupTask;
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
        ModuleProcessBroker moduleProcessBroker,
        StartupPreferenceSnapshot startupPreferences,
        StartupHealthJournal startupJournal,
        StartupPipelineCoordinator startupPipeline,
        ModuleTransactionRecoveryCoordinator moduleRecovery,
        SessionLogService sessionLog)
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
        _moduleProcessBroker = moduleProcessBroker;
        _startupPreferences = startupPreferences;
        _startupJournal = startupJournal;
        _startupPipeline = startupPipeline;
        _moduleRecovery = moduleRecovery;
        _sessionLog = sessionLog;
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
        _sessionLog.Information("Shell", "Main window loaded; critical startup presentation is beginning.");
        try
        {
            await PresentCriticalStartupAsync();
            _backgroundStartupTask = InitializeApplicationInBackgroundAsync();
        }
        catch (OperationCanceledException) when (_startupSession.State == StartupSessionState.Exiting)
        {
            // Application shutdown cancellation is an expected lifecycle outcome.
        }
        catch (Exception exception)
        {
            _sessionLog.Error("Shell", "Shell startup failed.", exception);
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
        _viewModel.ApplyStartupPreferences(_startupPreferences);
        await _startupSession.PresentAsync(_startupPreferences.PresentationMode);
        _startupJournal.Mark(StartupPhase.PresentationReady);
        if (_startupSession.StartupTestId is { } testId)
            _startupJournal.SetStartupTest(testId, StartupRegistrationTestStatus.PresentationReady);
        foreach (var pendingTestId in _startupSession.DrainStartupProbes())
            _startupJournal.RecordStartupTestResult(pendingTestId, StartupRegistrationTestStatus.AlreadyRunning);
    }

    private async Task InitializeApplicationInBackgroundAsync()
    {
        _sessionLog.Information("Startup", "Background startup pipeline started.");
        try
        {
            var token = _startupSession.LifetimeToken;
            UserSettings? settings = null;
            try { settings = await _settingsService.ReadAsync(token); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Debug.WriteLine($"Startup settings read degraded: {exception.GetType().Name}"); }

            if (settings is not null) _viewModel.InitializeLogSettings(settings);

            var startupSettings = settings;
            ModuleTransactionRecoveryOutcome? recoveryOutcome = null;
            var results = await _startupPipeline.RunAsync(
            [
                new("ModuleRecovery", async ct =>
                {
                    _startupSession.BeginModuleRecovery();
                    recoveryOutcome = await _moduleRecovery.RecoverAsync(ct);
                }),
                new("Registration", async ct =>
                {
                    if (startupSettings is null) throw new IOException("Startup settings unavailable.");
                    await _viewModel.ReconcileStartupRegistrationAsync(startupSettings, ct);
                    await _viewModel.InitializeStartupSettingsUiAsync(startupSettings, ct);
                }),
                new("Discovery", async ct =>
                {
                    _startupSession.BeginDiscovery();
                    await _viewModel.InitializeDiscoveryAsync(ct);
                }),
                new("Restore", async ct =>
                {
                    _startupSession.BeginModuleRestore();
                    await _moduleRecovery.RestoreDeferredRuntimeIntentsAsync(ct);
                    _viewModel.RefreshModuleExecutionReadiness();
                    await _viewModel.RestoreAuthorizedStartupModulesAsync(ct);
                })
            ], token);
            if (recoveryOutcome?.IsDegraded == true)
                _viewModel.StatusMessage = _viewModel.Strings["status.moduleRecoveryDegraded"];
            ApplyPipelineResult(results[1], StartupPhase.RegistrationHealthReady, "startup.registrationHealthDegraded");
            ApplyPipelineResult(results[2], StartupPhase.ModuleDiscoveryComplete, "startup.discoveryDegraded");
            ApplyPipelineResult(results[3], StartupPhase.AuthorizedModulesRestored, "startup.restoreDegraded");

            if (_startupSession.State != StartupSessionState.Exiting)
            {
                _startupSession.Complete();
                _startupJournal.Mark(StartupPhase.Ready);
                _sessionLog.Information("Startup", "Background startup pipeline completed.");
            }
        }
        catch(OperationCanceledException) when(_startupSession.State==StartupSessionState.Exiting){}
        catch(Exception exception)
        {
            _sessionLog.Error("Startup", "Background startup pipeline failed.", exception);
            System.Diagnostics.Debug.WriteLine($"Background startup failed: {exception.GetType().Name}");
            _viewModel.StatusMessage = _viewModel.Strings["status.startupFailed"];
        }
    }

    private void ApplyPipelineResult(StartupPipelineStageResult result, StartupPhase phase, string diagnostic)
    {
        if (result.Outcome == StartupPhaseOutcome.Succeeded)
        {
            _startupJournal.Mark(phase);
            _sessionLog.Information("Startup", $"Stage {phase} completed.");
        }
        else
        {
            _sessionLog.Warning("Startup", $"Stage {phase} degraded: {result.Error?.GetType().Name ?? diagnostic}.");
            System.Diagnostics.Debug.WriteLine($"Startup stage {phase} degraded: {result.Error?.GetType().Name}");
            _startupJournal.Mark(phase, StartupPhaseOutcome.Degraded, diagnostic);
            _viewModel.StatusMessage = _viewModel.Strings[diagnostic];
        }
    }

    internal async Task StopBackgroundStartupAsync()
    {
        var task = _backgroundStartupTask;
        if (task is null) return;
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
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

    internal async Task MinimizeMainWindowToNotificationAreaAsync()
    {
        if (!_notificationArea.Initialize())
        {
            EnsureMainWindowVisible();
            _viewModel.StatusMessage = _localization.GetString("notificationArea.unavailable");
            return;
        }
        _notificationAreaRestoreState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        _moduleWindowManager.SuspendForFloatingBadge();
        if (!await _moduleProcessBroker.SuspendWindowsAsync())
        {
            _moduleWindowManager.RestoreAfterFloatingBadge();
            EnsureMainWindowVisible();
            _sessionLog.Warning("ModuleProcess", "Notification-area transition aborted because a worker window could not be suspended.");
            _viewModel.StatusMessage = _localization.GetString("floatingBadge.restoreFailed");
            return;
        }
        ShowInTaskbar = false;
        Hide();
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
        if (!await _moduleProcessBroker.RestoreWindowsAsync())
        {
            _sessionLog.Warning("ModuleProcess", "One or more worker windows could not be restored from the notification area.");
            _viewModel.StatusMessage = _localization.GetString("floatingBadge.restoreFailed");
        }
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
