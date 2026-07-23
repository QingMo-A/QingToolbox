using System.Reflection;
using QingToolbox.Shell.Startup;
using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell.WebShell;

public sealed class WebAppSnapshotProvider(
    ApplicationExecutionEnvironment environment,
    MainWindowViewModel viewModel,
    TimeProvider timeProvider)
{
    public WebAppSnapshot Create() => new(
        environment.Kind.ToString(), environment.DisplayName,
        typeof(WebAppSnapshotProvider).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "unknown",
        WebBridgeProtocol.Version, viewModel.TotalModuleCount, viewModel.ValidModuleCount,
        viewModel.RunningModuleCount, timeProvider.GetUtcNow());
}
