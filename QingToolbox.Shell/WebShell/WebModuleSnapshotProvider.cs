using QingToolbox.Shell.ViewModels;
using QingToolbox.Core.Updates;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.WebShell;

public interface IWebModuleSnapshotSource
{
    IReadOnlyList<WebModuleSnapshotItem> ReadModules();
}

public sealed class WebModuleSnapshotSource(
    MainWindowViewModel viewModel,
    ApplicationExecutionEnvironment environment) : IWebModuleSnapshotSource
{
    public IReadOnlyList<WebModuleSnapshotItem> ReadModules() => viewModel.Modules.Select(module =>
        new WebModuleSnapshotItem(
            module.Id,
            module.DisplayName,
            module.DisplayDescription,
            module.Version,
            module.Author,
            module.RuntimeType,
            module.LoadMode,
            module.RuntimeState,
            module.IsValid,
            module.ErrorCount,
            SafeErrors(module),
            module.Module.Manifest.Permissions.Select(permission => permission.ToString()).ToArray(),
            module.MinimumHostVersion,
            module.IsUserInstalled,
            module.CanRemove,
            module.CanLoad,
            module.CanActivate,
            module.CanOpen,
            module.CanDeactivate,
            module.CanUnload,
            module.IsBusy,
            module.IsExecutionBlocked,
            module.IsStartupEnabled,
            module.StartupAuthorizationState.ToString(),
            module.CanChangeStartupAuthorization,
            module.IsStartupAuthorizationBusy,
            module.UpdateResult.Status.ToString(),
            module.UpdateResult.TargetVersion?.ToString(),
            string.IsNullOrWhiteSpace(module.DisplayUpdateReleaseNote) ? null : module.DisplayUpdateReleaseNote,
            module.UpdateResult.IsFromStaleCache,
            viewModel.CanCheckModuleUpdateFromWeb(module.Id),
            module.UpdateResult.Status == ModuleUpdateStatus.Checking,
            module.CanDownloadUpdate,
            module.DownloadStatus.ToString(),
            module.IsDownloadActive,
            Math.Max(0, module.DownloadBytesReceived),
            Math.Max(0, module.DownloadExpectedBytes),
            environment.IsDevelopment && viewModel.CanInstallVerifiedModuleUpdateFromWeb(module.Id))).ToArray();

    private static IReadOnlyList<string> SafeErrors(DiscoveredModuleViewModel module)
    {
        if (!module.HasErrors)
            return [];

        return module.Errors
            .Select((_, index) => index < module.Module.Errors.Count &&
                    !string.IsNullOrWhiteSpace(module.Module.Errors[index].Code)
                ? $"{module.Module.Errors[index].Code}: Module metadata validation failed."
                : "Module localization validation failed.")
            .Take(20)
            .ToArray();
    }
}

public sealed class WebModuleSnapshotProvider(IWebModuleSnapshotSource source, TimeProvider timeProvider)
{
    public WebModuleSnapshot Create() => new(timeProvider.GetUtcNow(), source.ReadModules());
}
