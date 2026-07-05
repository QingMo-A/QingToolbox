using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using QingToolbox.Core;
using QingToolbox.ModuleLoader;
using QingToolbox.Shell.ViewModels;

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
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();

        var viewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();
        await viewModel.RefreshModulesCommand.ExecuteAsync(null);

        _serviceProvider.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
