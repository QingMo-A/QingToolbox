using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell.WebShell;

public enum WebModuleUpdateOperationResult
{
    Succeeded,
    NotFound,
    Busy,
    Unavailable,
    Failed
}

public interface IWebModuleUpdateOperations
{
    Task<WebModuleUpdateOperationResult> CheckAsync(string moduleId, CancellationToken cancellationToken);
    Task<WebModuleUpdateOperationResult> DownloadAndVerifyAsync(string moduleId, CancellationToken cancellationToken);
}

public sealed class WebModuleUpdateOperations(MainWindowViewModel viewModel) : IWebModuleUpdateOperations
{
    public Task<WebModuleUpdateOperationResult> CheckAsync(string moduleId, CancellationToken cancellationToken) =>
        viewModel.CheckModuleUpdateFromWebAsync(moduleId, cancellationToken);

    public Task<WebModuleUpdateOperationResult> DownloadAndVerifyAsync(string moduleId, CancellationToken cancellationToken) =>
        viewModel.DownloadModuleUpdateFromWebAsync(moduleId, cancellationToken);
}
