using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell.WebShell;

public enum WebModuleManagementResult
{
    Succeeded,
    SucceededWithWarning,
    NotFound,
    Busy,
    Unavailable,
    ExecutionBlocked,
    Failed
}

public interface IWebModuleManagementOperations
{
    Task<WebModuleManagementResult> OpenDirectoryAsync(string moduleId, CancellationToken cancellationToken);
    Task<WebModuleManagementResult> RemoveAsync(string moduleId, CancellationToken cancellationToken);
}

public sealed class WebModuleManagementOperations(MainWindowViewModel viewModel)
    : IWebModuleManagementOperations
{
    public Task<WebModuleManagementResult> OpenDirectoryAsync(string moduleId, CancellationToken cancellationToken) =>
        viewModel.OpenModuleDirectoryFromWebAsync(moduleId, cancellationToken);

    public Task<WebModuleManagementResult> RemoveAsync(string moduleId, CancellationToken cancellationToken) =>
        viewModel.RemoveModuleFromWebAsync(moduleId, cancellationToken);
}
