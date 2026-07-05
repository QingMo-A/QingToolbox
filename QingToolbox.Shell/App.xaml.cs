using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using QingToolbox.Core;
using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var services = new ServiceCollection();
        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();
        _services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
