using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell.WebShell;

public interface IWebModuleStartupAuthorizationOperations
{
    Task<WebModuleLifecycleResult> SetAsync(string moduleId, bool enabled, CancellationToken cancellationToken);
}

public sealed class WebModuleStartupAuthorizationOperations(MainWindowViewModel viewModel)
    : IWebModuleStartupAuthorizationOperations
{
    public Task<WebModuleLifecycleResult> SetAsync(string moduleId, bool enabled, CancellationToken cancellationToken) =>
        viewModel.SetModuleStartupAuthorizationFromWebAsync(moduleId, enabled, cancellationToken);
}
