using System.Windows;
using System.IO;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var launchOptions = ApplicationLaunchOptions.Parse(e.Args);
        _singleInstance = SingleInstanceCoordinator.Create();
        if (!_singleInstance.IsPrimary)
        {
            await _singleInstance.SendAsync(launchOptions.IsStartupLaunch
                ? InstanceActivationMessage.StartupProbe
                : InstanceActivationMessage.Activate);
            Shutdown();
            return;
        }

        var services = new ServiceCollection();
        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<ModuleManifestReader>();
        services.AddSingleton<ModuleManifestValidator>();
        services.AddSingleton<ModuleManifestScanner>();
        services.AddSingleton<InProcessModuleLoader>();
        services.AddSingleton<ModuleRuntimeManager>();
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton(launchOptions);
        services.AddSingleton<IStartupRegistrationStore, WindowsRunRegistrationStore>();
        services.AddSingleton<WindowsStartupRegistrationService>();
        services.AddSingleton<ModuleStartupFingerprintService>();
        services.AddSingleton<LocalizationManager>();
        services.AddSingleton<ILocalizationService>(
            provider => provider.GetRequiredService<LocalizationManager>());
        services.AddSingleton<ModuleWindowManager>();
        services.AddSingleton<FloatingBadgeManager>();
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
        _singleInstance.MessageReceived += message => Dispatcher.InvokeAsync(async () =>
        {
            if (message == InstanceActivationMessage.Activate)
            {
                await _serviceProvider.GetRequiredService<FloatingBadgeManager>().RestoreAsync();
                if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal;
                mainWindow.Show();
                mainWindow.Activate();
                mainWindow.Focus();
            }
        }).Task.Unwrap();
        _singleInstance.StartServer();

        if (launchOptions.IsStartupLaunch) { mainWindow.Opacity = 0; mainWindow.ShowActivated = false; }
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _serviceProvider?.GetService<FloatingBadgeManager>()?.PrepareForApplicationExit();
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
                _singleInstance?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
        _serviceProvider?.GetService<FloatingBadgeManager>()?.PrepareForApplicationExit();
        base.OnSessionEnding(e);
    }
}
