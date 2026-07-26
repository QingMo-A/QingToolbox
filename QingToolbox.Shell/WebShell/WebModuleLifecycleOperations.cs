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
    Task<WebModuleLifecycleResult> OpenAsync(string moduleId, CancellationToken cancellationToken);
    Task<WebModuleLifecycleResult> DeactivateAsync(string moduleId, CancellationToken cancellationToken);
    Task<WebModuleLifecycleResult> UnloadAsync(string moduleId, CancellationToken cancellationToken);
}

public sealed class WebModuleLifecycleOperations(MainWindowViewModel viewModel) : IWebModuleLifecycleOperations
{
    public Task<WebModuleLifecycleResult> LoadAsync(string moduleId, CancellationToken cancellationToken) =>
        viewModel.LoadModuleFromWebAsync(moduleId, cancellationToken);

    public Task<WebModuleLifecycleResult> ActivateAsync(string moduleId, CancellationToken cancellationToken) =>
        viewModel.ActivateModuleFromWebAsync(moduleId, cancellationToken);

    public Task<WebModuleLifecycleResult> OpenAsync(string moduleId, CancellationToken cancellationToken) =>
        viewModel.OpenModuleFromWebAsync(moduleId, cancellationToken);
    public Task<WebModuleLifecycleResult> DeactivateAsync(string moduleId, CancellationToken cancellationToken) =>
        viewModel.DeactivateModuleFromWebAsync(moduleId, cancellationToken);
    public Task<WebModuleLifecycleResult> UnloadAsync(string moduleId, CancellationToken cancellationToken) =>
        viewModel.UnloadModuleFromWebAsync(moduleId, cancellationToken);
}
