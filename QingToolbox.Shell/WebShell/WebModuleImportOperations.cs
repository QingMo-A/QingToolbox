using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell.WebShell;

public enum WebModuleImportDisposition
{
    Imported,
    Cancelled
}

public sealed record WebModuleImportOperationResult(WebModuleImportDisposition Disposition, string? ImportedModuleId);

public interface IWebModuleImportOperations
{
    Task<WebModuleImportOperationResult> ImportAsync(CancellationToken cancellationToken);
}

public sealed class WebModuleImportOperations(MainWindowViewModel viewModel) : IWebModuleImportOperations
{
    public Task<WebModuleImportOperationResult> ImportAsync(CancellationToken cancellationToken) =>
        viewModel.ImportModuleFromWebAsync(cancellationToken);
}
