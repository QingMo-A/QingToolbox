using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell.WebShell;

public interface IWebModuleSnapshotSource
{
    IReadOnlyList<WebModuleSnapshotItem> ReadModules();
}

public sealed class WebModuleSnapshotSource(MainWindowViewModel viewModel) : IWebModuleSnapshotSource
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
            module.IsUserInstalled)).ToArray();

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
