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

namespace QingToolbox.Shell;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<ModuleManifestReader>();
        services.AddSingleton<ModuleManifestValidator>();
        services.AddSingleton<ModuleManifestScanner>();
        services.AddSingleton<InProcessModuleLoader>();
        services.AddSingleton<ModuleRuntimeManager>();
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton<LocalizationManager>();
        services.AddSingleton<ILocalizationService>(
            provider => provider.GetRequiredService<LocalizationManager>());
        services.AddSingleton<ModuleWindowManager>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();

        var localizationDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "Localization");
        await _serviceProvider
            .GetRequiredService<LocalizationManager>()
            .InitializeAsync(localizationDirectory);

        _serviceProvider.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
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
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to dispose application services during shutdown: {exception}");
            }

            base.OnExit(e);
        }
    }
}
