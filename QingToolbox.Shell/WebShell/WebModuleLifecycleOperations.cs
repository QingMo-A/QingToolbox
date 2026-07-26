using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell.WebShell;

public enum WebModuleLifecycleResult
{
    Succeeded,
    NotFound,
    Busy,
    Unavailable,
    ExecutionBlocked,
    Failed
}

public interface IWebModuleLifecycleOperations
{
    Task<WebModuleLifecycleResult> LoadAsync(string moduleId, CancellationToken cancellationToken);
    Task<WebModuleLifecycleResult> ActivateAsync(string moduleId, CancellationToken cancellationToken);
}

public sealed class WebModuleLifecycleOperations(MainWindowViewModel viewModel) : IWebModuleLifecycleOperations
{
    public Task<WebModuleLifecycleResult> LoadAsync(string moduleId, CancellationToken cancellationToken) =>
        viewModel.LoadModuleFromWebAsync(moduleId, cancellationToken);

    public Task<WebModuleLifecycleResult> ActivateAsync(string moduleId, CancellationToken cancellationToken) =>
        viewModel.ActivateModuleFromWebAsync(moduleId, cancellationToken);
}
