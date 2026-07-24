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
using System.Diagnostics;
using QingToolbox.Shell.WebShell;

namespace QingToolbox.Shell;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private SingleInstanceCoordinator? _singleInstance;
    private StartupSessionCoordinator? _startupSession;
    private Func<InstanceActivationRequest, Task>? _activationHandler;
    private int _activationDispatchPending;
    private SessionLogService? _sessionLog;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var processStartedAt = TimeProvider.System.GetUtcNow();
        var monotonicOrigin = Stopwatch.GetTimestamp();
        DateTimeOffset? instanceReadyAt = null;
        long? instanceReadyTimestamp = null;
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
                    ? new InstanceActivationRequest(InstanceActivationMessage.StartupProbe, launchOptions.StartupTestId)
                    : new InstanceActivationRequest(InstanceActivationMessage.Activate));
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
            _activationHandler = request =>
            {
                if (request.Message == InstanceActivationMessage.StartupProbe)
                {
                    if (request.StartupTestId is { } id && _serviceProvider?.GetService<StartupHealthJournal>() is { } journal)
                        journal.RecordStartupTestResult(id, StartupRegistrationTestStatus.AlreadyRunning);
                    else
                        _startupSession.RecordStartupProbe(request.StartupTestId);
                    return Task.CompletedTask;
                }
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
            _singleInstance.RequestReceived += _activationHandler;
            _singleInstance.StartServer();
            instanceReadyAt = TimeProvider.System.GetUtcNow();
            instanceReadyTimestamp = Stopwatch.GetTimestamp();

            var applicationPaths = new ApplicationPaths(environment);
            applicationPaths.EnsureDirectories();
            var startupPreferenceReader = new StartupPreferenceReader();
            var startupPreferences = await startupPreferenceReader.ReadAsync(
                applicationPaths.SettingsPath, launchOptions.IsStartupLaunch);
            var services = new ServiceCollection();
            services.AddSingleton<ModuleRegistry>();
            services.AddSingleton<ModuleManifestReader>();
            services.AddSingleton<ModuleManifestValidator>();
            services.AddSingleton<ModuleManifestScanner>();
            services.AddSingleton<ModuleDiscoveryCoordinator>();
            services.AddSingleton<InProcessModuleLoader>();
            services.AddSingleton<ModuleRuntimeManager>();
            services.AddSingleton(launchOptions);
            services.AddSingleton(environment);
            services.AddSingleton(_startupSession);
            services.AddSingleton<StartupPipelineCoordinator>();
            services.AddSingleton<IStartupRegistrationStore, WindowsRunRegistrationStore>();
            services.AddSingleton<WindowsRunStartupBackend>();
            services.AddSingleton<ITaskSchedulerStore, WindowsTaskSchedulerStore>();
            services.AddSingleton<WindowsTaskSchedulerStartupBackend>();
            services.AddSingleton<WindowsStartupRegistrationService>();
            services.AddSingleton<StartupTestCoordinator>();
            services.AddSingleton<IStartupTestCoordinator>(provider => provider.GetRequiredService<StartupTestCoordinator>());
            services.AddSingleton(startupPreferenceReader);
            services.AddSingleton(startupPreferences);
            services.AddSingleton<ModuleStartupFingerprintService>();
            services.AddSingleton<LocalizationManager>();
            services.AddSingleton<ILocalizationService>(
                provider => provider.GetRequiredService<LocalizationManager>());
            services.AddSingleton<ModuleWindowManager>();
            services.AddSingleton<ModuleWindowPresentationCoordinator>();
            services.AddSingleton<FloatingBadgeManager>();
            services.AddSingleton<NotificationAreaService>();
            services.AddSingleton<INotificationAreaIcon>(provider => provider.GetRequiredService<NotificationAreaService>());
            services.AddSingleton<ApplicationExitCoordinator>();
            services.AddSingleton(applicationPaths);
            services.AddSingleton<SessionLogService>();
            services.AddSingleton<ModuleProcessBroker>();
            services.AddSingleton(provider => new ModuleTransactionRecoveryGate(entry =>
            {
                provider.GetRequiredService<SessionLogService>().Warning(
                    "ModuleRecovery",
                    $"{entry.EventName}; module={entry.ModuleId}; state={entry.State}; " +
                    $"failure={entry.FailureCode}.");
            }));
            services.AddSingleton<IModuleExecutionReadinessGate>(provider =>
                provider.GetRequiredService<ModuleTransactionRecoveryGate>().Consumer);
            services.AddSingleton<ModuleUpdateRuntimeCoordinator>();
            services.AddSingleton<IModuleUpdateRuntimeCoordinator>(provider =>
                provider.GetRequiredService<ModuleUpdateRuntimeCoordinator>());
            services.AddSingleton(provider => new QmodPackageStagingService(
                provider.GetRequiredService<ApplicationPaths>().QmodStagingDirectory,
                provider.GetRequiredService<TimeProvider>(),
                environment.Kind.ToString(),
                provider.GetRequiredService<ApplicationPaths>().UserModulesDirectory,
                log: entry =>
                {
                    var logger = provider.GetRequiredService<SessionLogService>();
                    var message = $"{entry.EventName}; module={entry.ModuleId}; version={entry.Version}; " +
                                  $"package={entry.PackageHashPrefix}; failure={entry.FailureCode}; " +
                                  $"entries={entry.EntryCount}; bytes={entry.TotalUncompressedBytes}.";
                    if (entry.FailureCode == QmodStagingFailureCode.None) logger.Information("QmodStaging", message);
                    else logger.Warning("QmodStaging", message);
                }));
            services.AddSingleton<IQmodVerifiedStagingAttestor>(provider =>
                provider.GetRequiredService<QmodPackageStagingService>());
            if (!environment.IsProduction)
            {
                services.AddSingleton(provider => new ModuleUpdateTransactionService(
                    environment.Kind.ToString(),
                    provider.GetRequiredService<ApplicationPaths>().UserModulesDirectory,
                    provider.GetRequiredService<ApplicationPaths>().ModuleTransactionsDirectory,
                    ModuleUpdateIdentity.ModuleApiVersion,
                    provider.GetRequiredService<IQmodVerifiedStagingAttestor>(),
                    provider.GetRequiredService<IModuleUpdateRuntimeCoordinator>(),
                    entry =>
                    {
                        var message = $"{entry.EventName}; module={entry.ModuleId}; " +
                                      $"source={entry.SourceVersion}; target={entry.TargetVersion}; " +
                                      $"transaction={entry.TransactionIdPrefix}; state={entry.State}; " +
                                      $"failure={entry.FailureCode}.";
                        var logger = provider.GetRequiredService<SessionLogService>();
                        if (entry.FailureCode == ModuleUpdateTransactionFailureCode.None)
                            logger.Information("ModuleTransaction", message);
                        else
                            logger.Warning("ModuleTransaction", message);
                    }));
                services.AddSingleton<GatedModuleUpdateTransactionCoordinator>();
            }
            services.AddSingleton<ModuleTransactionRecoveryCoordinator>();
            services.AddSingleton(provider => new StartupHealthJournal(
                provider.GetRequiredService<ApplicationPaths>().StartupHealthPath,
                provider.GetRequiredService<TimeProvider>(), processStartedAt, monotonicOrigin,
                instanceReadyAt, instanceReadyTimestamp,
                launchOptions.StartupTestId ?? Guid.NewGuid(), launchOptions.StartupSource,
                launchOptions.StartupTestId));
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
            if (environment.IsDevelopment)
            {
                services.AddSingleton<WebShellState>();
                services.AddSingleton<WebNavigationPolicy>();
                services.AddSingleton(_ => new Lazy<WebAssetIdentity>(() =>
                    new WebAssetIdentity(Path.Combine(AppContext.BaseDirectory, "WebUI")), true));
                services.AddSingleton<WebActivationSession>();
                services.AddSingleton<WebAppSnapshotProvider>();
                services.AddSingleton<IWebCommandHandler, WebPingCommandHandler>();
                services.AddSingleton<IWebCommandHandler, WebSnapshotCommandHandler>();
                services.AddSingleton<IWebCommandHandler, WebReadyCommandHandler>();
                services.AddSingleton<WebBridgeDispatcher>();
                services.AddSingleton<WebBridgeHost>();
                services.AddSingleton<WebShellInitializer>();
                services.AddSingleton<IWebShellInitializer>(provider =>
                    provider.GetRequiredService<WebShellInitializer>());
            }
            else services.AddSingleton<IWebShellInitializer, DisabledWebShellInitializer>();
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();

            _serviceProvider = services.BuildServiceProvider();
            _sessionLog = _serviceProvider.GetRequiredService<SessionLogService>();
            _sessionLog.Information("Application",
                $"Session started. Environment={environment.Kind}; Version={typeof(App).Assembly.GetName().Version}");
            DispatcherUnhandledException += (_, args) =>
                _sessionLog?.Error("UnhandledException", "A dispatcher exception reached the application boundary.", args.Exception);
            TaskScheduler.UnobservedTaskException += (_, args) =>
                _sessionLog?.Error("UnobservedTask", "An unobserved task exception was reported.", args.Exception);
            var startupJournal = _serviceProvider.GetRequiredService<StartupHealthJournal>();
            startupJournal.Mark(StartupPhase.InstanceReady);

            var localizationDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "Resources",
                "Localization");
            await _serviceProvider
                .GetRequiredService<LocalizationManager>()
                .InitializeAsync(localizationDirectory, startupPreferences.Language, _startupSession.LifetimeToken);
            startupJournal.Mark(StartupPhase.MinimalServicesReady);

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
            notificationArea.AvailabilityChanged += (_, change) =>
            {
                if (change.RecoveredAfterExplorerRestart) startupJournal.RecordNotificationRecovery();
                else if (!change.Available)
                    startupJournal.Mark(StartupPhase.NotificationAreaReady, StartupPhaseOutcome.Degraded,
                        change.FailureDiagnostic ?? "startup.notificationRecoveryFailed");
                viewModel.ApplyNotificationAvailability(change);
            };
            var notificationReady = notificationArea.Initialize();
            startupJournal.Mark(StartupPhase.NotificationAreaReady,
                notificationReady ? StartupPhaseOutcome.Succeeded : StartupPhaseOutcome.Degraded,
                notificationReady ? null : "startup.notificationUnavailable");
            if (launchOptions.IsStartupLaunch) { mainWindow.Opacity = 0; mainWindow.ShowActivated = false; }
            mainWindow.Show();
            if (!notificationReady)
                _ = RetryNotificationAreaObservedAsync(notificationArea, startupJournal, _startupSession.LifetimeToken);
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
        _sessionLog?.Information("Application", $"Application exit requested. ExitCode={e.ApplicationExitCode}");
        try { _serviceProvider?.GetService<MainWindowViewModel>()?.StopUpdateChecks(); } catch { }
        try { _startupSession?.PrepareForExit(); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Final startup cleanup failed: {exception.GetType().Name}"); }
        try { _serviceProvider?.GetService<MainWindow>()?.StopBackgroundStartupAsync().Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Background startup cleanup failed: {exception.GetType().Name}"); }
        try
        {
            if (_singleInstance is not null)
            {
                if (_activationHandler is not null)
                    _singleInstance.RequestReceived -= _activationHandler;
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
            _serviceProvider?.GetService<StartupHealthJournal>()?.Mark(StartupPhase.Exiting);
            _serviceProvider?.GetService<StartupHealthJournal>()?.FlushAsync()
                .WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            _serviceProvider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _startupSession?.Dispose();
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Final service cleanup failed: {exception.GetType().Name}"); }
        _sessionLog = null;
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
            _singleInstance.RequestReceived -= _activationHandler;
        await _singleInstance.DisposeAsync();
        _singleInstance = null;
    }

    private static async Task RetryNotificationAreaObservedAsync(
        INotificationAreaIcon notificationArea, StartupHealthJournal journal, CancellationToken token)
    {
        for(var attempt=1;attempt<5&&!token.IsCancellationRequested&&!notificationArea.IsAvailable;attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), token);
                if (notificationArea.Initialize())
                {
                    journal.Mark(StartupPhase.NotificationAreaReady, StartupPhaseOutcome.Succeeded,
                        "startup.notificationRecovered");
                    return;
                }
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){return;}
            catch(Exception exception){System.Diagnostics.Debug.WriteLine($"Notification retry failed: {exception.GetType().Name}");}
        }
        journal.Mark(StartupPhase.NotificationAreaReady, StartupPhaseOutcome.Degraded,
            "startup.notificationRetryExhausted");
    }
}
