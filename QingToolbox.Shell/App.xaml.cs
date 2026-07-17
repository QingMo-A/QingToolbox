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
using QingToolbox.Core.Updates;
using System.Net.Http.Headers;
using System.Reflection;
using System.Net.Http;
using System.Net;

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

        ApplicationLaunchOptions launchOptions;
        try
        {
#if DEBUG
            launchOptions = ApplicationLaunchOptions.Parse(e.Args, requireExplicitEnvironment: true);
#else
            launchOptions = ApplicationLaunchOptions.Parse(e.Args);
#endif
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"Invalid QingToolbox launch options: {exception.Message}");
            var chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
            MessageBox.Show(chinese
                    ? $"启动参数无效：{exception.Message}\n请使用 scripts/start-dev-host.ps1 启动开发环境。"
                    : $"Invalid launch options: {exception.Message}\nUse scripts/start-dev-host.ps1 for development.",
                "QingToolbox", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = (int)StartupExitCode.InvalidArguments;
            Shutdown(Environment.ExitCode);
            return;
        }
        var environment = launchOptions.Environment;
        System.Diagnostics.Debug.WriteLine($"QingToolbox environment: {environment.Kind}; profile: {environment.ProfileName}; sandbox: {environment.SandboxRoot ?? "<production>"}");
        _singleInstance = environment.IsProduction
            ? SingleInstanceCoordinator.Create()
            : SingleInstanceCoordinator.CreateForScope(environment.InstanceScope);
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
            Environment.ExitCode = delivered ? (int)StartupExitCode.Success : (int)StartupExitCode.SingleInstanceDeliveryFailure;
            Shutdown(Environment.ExitCode);
            return;
        }

        try
        {

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
            services.AddSingleton(launchOptions);
            services.AddSingleton(environment);
            services.AddSingleton(_startupSession);
            services.AddSingleton<IStartupRegistrationStore, WindowsRunRegistrationStore>();
            services.AddSingleton<WindowsRunStartupBackend>();
            services.AddSingleton<ITaskSchedulerStore, WindowsTaskSchedulerStore>();
            services.AddSingleton<WindowsTaskSchedulerStartupBackend>();
            services.AddSingleton<WindowsStartupRegistrationService>();
            services.AddSingleton<StartupPreferenceReader>();
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
            services.AddSingleton(provider => new StartupHealthJournal(
                provider.GetRequiredService<ApplicationPaths>().StartupHealthPath,
                provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton(provider => new UserSettingsService(
                provider.GetRequiredService<ApplicationPaths>().SettingsPath));
            services.AddSingleton<ModulePackageImporter>();
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton(provider =>
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false };
                var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QingToolbox", version));
                return client;
            });
            services.AddSingleton<IModuleUpdateSource>(provider => new OfficialModuleUpdateSource(
                provider.GetRequiredService<HttpClient>(), environment.IsModuleTest ? null :
                new ModuleUpdateCache(Path.Combine(provider.GetRequiredService<ApplicationPaths>().CacheDirectory, "ModuleUpdates", "Official"), provider.GetRequiredService<TimeProvider>()),
                provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton(provider =>
            {
                var text = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
                if (!SemanticVersion.TryParse(text, out var version)) throw new InvalidOperationException("Host informational version is not valid SemVer.");
                return new ModuleUpdateCompatibilityEvaluator(version!, ModuleUpdateIdentity.ModuleApiVersion,
                    version!.IsPrerelease ? ModuleUpdateChannel.Preview : ModuleUpdateChannel.Stable);
            });
            services.AddSingleton(provider => new ModuleUpdateChecker(provider.GetRequiredService<IModuleUpdateSource>(),
                provider.GetRequiredService<ModuleUpdateCompatibilityEvaluator>(), provider.GetRequiredService<TimeProvider>(), environment.IsModuleTest));
            services.AddSingleton<IModuleUpdateChecker>(provider => provider.GetRequiredService<ModuleUpdateChecker>());
            services.AddSingleton<ModuleUpdateCheckCoordinator>();
            services.AddSingleton<IModulePackageTransport>(_ =>
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false };
                var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QingToolbox", version));
                return new OfficialModulePackageTransport(client, ownsClient: true);
            });
            services.AddSingleton(provider => new ModulePackageDownloadCoordinator(
                provider.GetRequiredService<IModuleUpdateChecker>(), provider.GetRequiredService<IModulePackageTransport>(),
                provider.GetRequiredService<ApplicationPaths>().CacheDirectory, provider.GetRequiredService<TimeProvider>(), environment.IsModuleTest));
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();

            _serviceProvider = services.BuildServiceProvider();
            _serviceProvider.GetRequiredService<ApplicationPaths>().EnsureDirectories();
            var startupJournal = _serviceProvider.GetRequiredService<StartupHealthJournal>();
            startupJournal.SetSource(launchOptions.StartupSource);
            startupJournal.Mark(StartupPhase.InstanceReady);

            var localizationDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "Resources",
                "Localization");
            await _serviceProvider
                .GetRequiredService<LocalizationManager>()
                .InitializeAsync(localizationDirectory);

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            var notificationArea = _serviceProvider.GetRequiredService<INotificationAreaIcon>();
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
            notificationArea.FloatingBadgeRequested = mainWindow.SwitchToFloatingBadgeFromNotificationAreaAsync;
            notificationArea.ExitRequested = () => exitCoordinator.RequestExitAsync(ApplicationExitReason.NotificationAreaMenu);
            var notificationReady = notificationArea.Initialize();
            startupJournal.Mark(StartupPhase.NotificationAreaReady,
                notificationReady ? null : "startup.notificationUnavailable");
            if (launchOptions.IsStartupLaunch) { mainWindow.Opacity = 0; mainWindow.ShowActivated = false; }
            mainWindow.Show();
            if (!notificationReady) _ = RetryNotificationAreaObservedAsync(notificationArea, _startupSession.LifetimeToken);
            if (_startupSession.ManualActivationRequested) await _startupSession.ActivateMainWindowAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Application startup failed: {exception.GetType().Name}");
            Environment.ExitCode = (int)StartupExitCode.FatalInitializationFailure;
            _serviceProvider?.GetService<StartupHealthJournal>()?.Fail(StartupPhase.MinimalServicesReady,
                "startup.fatalInitialization", Environment.ExitCode);
            Shutdown(Environment.ExitCode);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _serviceProvider?.GetService<MainWindowViewModel>()?.StopUpdateChecks(); } catch { }
        try { _startupSession?.PrepareForExit(); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Final startup cleanup failed: {exception.GetType().Name}"); }
        try
        {
            if (_singleInstance is not null)
            {
                if (_activationHandler is not null)
                    _singleInstance.MessageReceived -= _activationHandler;
                _singleInstance.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _singleInstance = null;
            }
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Final activation cleanup failed: {exception.GetType().Name}"); }
        try { _serviceProvider?.GetService<FloatingBadgeManager>()?.PrepareForApplicationExit(); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Final badge cleanup failed: {exception.GetType().Name}"); }
        try { _serviceProvider?.GetService<INotificationAreaIcon>()?.Dispose(); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Final notification cleanup failed: {exception.GetType().Name}"); }
        try
        {
            var exitCoordinator = _serviceProvider?.GetService<ApplicationExitCoordinator>();
            if (exitCoordinator?.RuntimeCleanupCompleted != true)
                _serviceProvider?.GetService<ModuleRuntimeManager>()?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Final runtime cleanup failed: {exception.GetType().Name}"); }
        try
        {
            _serviceProvider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _startupSession?.Dispose();
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Final service cleanup failed: {exception.GetType().Name}"); }
        base.OnExit(e);
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

    private static async Task RetryNotificationAreaObservedAsync(INotificationAreaIcon notificationArea,CancellationToken token)
    {
        for(var attempt=1;attempt<5&&!token.IsCancellationRequested&&!notificationArea.IsAvailable;attempt++)
        {
            try{await Task.Delay(TimeSpan.FromMilliseconds(200*attempt),token);notificationArea.Initialize();}
            catch(OperationCanceledException)when(token.IsCancellationRequested){return;}
            catch(Exception exception){System.Diagnostics.Debug.WriteLine($"Notification retry failed: {exception.GetType().Name}");}
        }
    }
}
