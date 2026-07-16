using System.Windows;
using System.IO;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using QingToolbox.Core;
using QingToolbox.Core.Runtime;
using QingToolbox.ModuleLoader;
using QingToolbox.Shell.ViewModels;
using QingToolbox.Shell.Services;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Core.Localization;
using QingToolbox.Core.Settings;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private SingleInstanceCoordinator? _singleInstance;
    private StartupSessionCoordinator? _startupSession;
    private Func<InstanceActivationMessage, Task>? _activationHandler;
    private int _activationDispatchPending;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var launchOptions = ApplicationLaunchOptions.Parse(e.Args);
        _singleInstance = SingleInstanceCoordinator.Create();
        if (!_singleInstance.IsPrimary)
        {
            var delivered = false;
            try
            {
                delivered = await _singleInstance.SendAsync(launchOptions.IsStartupLaunch
                    ? InstanceActivationMessage.StartupProbe
                    : InstanceActivationMessage.Activate);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Single-instance delivery failed: {exception.GetType().Name}");
            }
            if (!delivered && !launchOptions.IsStartupLaunch)
            {
                System.Diagnostics.Debug.WriteLine("Existing QingToolbox instance could not be activated.");
                var chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
                MessageBox.Show(
                    chinese
                        ? "QingToolbox 已在运行，但暂时无法激活现有窗口。"
                        : "QingToolbox is already running, but its window could not be activated.",
                    "QingToolbox",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        _startupSession = new StartupSessionCoordinator(launchOptions);
        _activationHandler = message =>
        {
            if (message != InstanceActivationMessage.Activate) return Task.CompletedTask;
            if (!_startupSession.TryRequestManualActivation() || _serviceProvider is null)
                return Task.CompletedTask;
            if (Interlocked.Exchange(ref _activationDispatchPending, 1) != 0)
                return Task.CompletedTask;
            return Dispatcher.InvokeAsync(async () =>
            {
                try { await _startupSession.ActivateMainWindowAsync(); }
                finally { Interlocked.Exchange(ref _activationDispatchPending, 0); }
            }).Task.Unwrap();
        };
        _singleInstance.MessageReceived += _activationHandler;
        _singleInstance.StartServer();

        var services = new ServiceCollection();
        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<ModuleManifestReader>();
        services.AddSingleton<ModuleManifestValidator>();
        services.AddSingleton<ModuleManifestScanner>();
        services.AddSingleton<InProcessModuleLoader>();
        services.AddSingleton<ModuleRuntimeManager>();
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton(launchOptions);
        services.AddSingleton(_startupSession);
        services.AddSingleton<IStartupRegistrationStore, WindowsRunRegistrationStore>();
        services.AddSingleton<WindowsStartupRegistrationService>();
        services.AddSingleton<ModuleStartupFingerprintService>();
        services.AddSingleton<LocalizationManager>();
        services.AddSingleton<ILocalizationService>(
            provider => provider.GetRequiredService<LocalizationManager>());
        services.AddSingleton<ModuleWindowManager>();
        services.AddSingleton<FloatingBadgeManager>();
        services.AddSingleton<NotificationAreaService>();
        services.AddSingleton<INotificationAreaIcon>(provider => provider.GetRequiredService<NotificationAreaService>());
        services.AddSingleton<ApplicationExitCoordinator>();
        services.AddSingleton<ApplicationPaths>();
        services.AddSingleton<ModulePackageImporter>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.GetRequiredService<ApplicationPaths>().EnsureUserDirectories();

        var localizationDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "Localization");
        await _serviceProvider
            .GetRequiredService<LocalizationManager>()
            .InitializeAsync(localizationDirectory);

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        var notificationArea = _serviceProvider.GetRequiredService<NotificationAreaService>();
        var exitCoordinator = _serviceProvider.GetRequiredService<ApplicationExitCoordinator>();
        var badgeManager = _serviceProvider.GetRequiredService<FloatingBadgeManager>();
        var viewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();
        exitCoordinator.ConfigureStopActivation(StopAcceptingActivationAsync);
        badgeManager.ConfigureApplicationExit(() => exitCoordinator.RequestExitAsync(ApplicationExitReason.FloatingBadgeMenu));
        notificationArea.OpenRequested = mainWindow.RestoreMainWindowAsync;
        notificationArea.OpenSettingsRequested = async () =>
        {
            await mainWindow.RestoreMainWindowAsync();
            viewModel.SelectedNavigationKey = "Settings";
        };
        notificationArea.FloatingBadgeRequested = async () =>
        {
            await mainWindow.RestoreMainWindowAsync();
            await badgeManager.EnterAsync();
        };
        notificationArea.ExitRequested = () => exitCoordinator.RequestExitAsync(ApplicationExitReason.NotificationAreaMenu);
        notificationArea.Initialize();
        if (launchOptions.IsStartupLaunch) { mainWindow.Opacity = 0; mainWindow.ShowActivated = false; }
        mainWindow.Show();
        if (_startupSession.ManualActivationRequested) await _startupSession.ActivateMainWindowAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _startupSession?.PrepareForExit();
            if (_singleInstance is not null)
            {
                if (_activationHandler is not null)
                    _singleInstance.MessageReceived -= _activationHandler;
                _singleInstance.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _singleInstance = null;
            }
            _serviceProvider?.GetService<FloatingBadgeManager>()?.PrepareForApplicationExit();
            _serviceProvider?.GetService<NotificationAreaService>()?.Dispose();
            var runtimeManager = _serviceProvider?.GetService<ModuleRuntimeManager>();
            runtimeManager?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Failed to unload modules during shutdown: {exception}");
        }
        finally
        {
            try
            {
                _serviceProvider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _startupSession?.Dispose();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to dispose application services during shutdown: {exception}");
            }

            base.OnExit(e);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        var coordinator = _serviceProvider?.GetService<ApplicationExitCoordinator>();
        if (coordinator is not null) _ = coordinator.RequestExitAsync(ApplicationExitReason.SessionEnding);
        else _startupSession?.PrepareForExit();
        base.OnSessionEnding(e);
    }

    private async Task StopAcceptingActivationAsync()
    {
        if (_singleInstance is null) return;
        if (_activationHandler is not null)
            _singleInstance.MessageReceived -= _activationHandler;
        await _singleInstance.DisposeAsync();
        _singleInstance = null;
    }
}
