using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell.WebShell;

public enum WebModuleUpdateInstallOperationStatus
{
    Installed,
    RolledBack,
    RecoveryRequired,
    NotFound,
    Busy,
    Unavailable,
    Failed
}

public sealed record WebModuleUpdateInstallOperationResult(
    WebModuleUpdateInstallOperationStatus Status,
    string? SourceVersion = null,
    string? TargetVersion = null);

public interface IWebModuleUpdateInstallOperations
{
    Task<WebModuleUpdateInstallOperationResult> InstallAsync(
        string moduleId, CancellationToken cancellationToken);
}

public sealed class WebModuleUpdateInstallOperations(MainWindowViewModel viewModel)
    : IWebModuleUpdateInstallOperations
{
    public Task<WebModuleUpdateInstallOperationResult> InstallAsync(
        string moduleId, CancellationToken cancellationToken) =>
        viewModel.InstallVerifiedModuleUpdateFromWebAsync(moduleId, cancellationToken);
}
