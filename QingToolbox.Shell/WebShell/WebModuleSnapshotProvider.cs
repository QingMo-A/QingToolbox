using QingToolbox.Shell.ViewModels;
using QingToolbox.Core.Updates;
using QingToolbox.Shell.Startup;
using System.IO;
using System.Security;

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
            environment.IsDevelopment && viewModel.CanInstallVerifiedModuleUpdateFromWeb(module.Id),
            WebModuleIconProjection.ReadDataUri(module.ModuleDirectory, module.IconPath))).ToArray();

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

/// <summary>
/// Projects a module manifest icon into a bounded, host-controlled image value for WebUI.
/// The Web bridge never exposes the module directory or the source icon path.
/// </summary>
public static class WebModuleIconProjection
{
    public const int MaximumIconBytes = 256 * 1024;
    public const string DataUriPrefix = "data:image/svg+xml;base64,";

    public static string? ReadDataUri(string? moduleDirectory, string? iconPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(moduleDirectory) || string.IsNullOrWhiteSpace(iconPath))
                return null;

            var moduleRoot = Path.GetFullPath(moduleDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(iconPath);
            var modulePrefix = moduleRoot + Path.DirectorySeparatorChar;

            // IconPath is already resolved by DiscoveredModuleViewModel, but repeat the
            // boundary and extension checks at the Web projection boundary as defense in depth.
            if (!candidate.StartsWith(modulePrefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(candidate), ".svg", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(candidate))
                return null;

            if (ContainsReparsePoint(moduleRoot, candidate))
                return null;
            var attributes = File.GetAttributes(candidate);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return null;

            var info = new FileInfo(candidate);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumIconBytes)
                return null;

            var bytes = File.ReadAllBytes(candidate);
            if (ContainsReparsePoint(moduleRoot, candidate) ||
                (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0 ||
                bytes.Length <= 0 || bytes.Length > MaximumIconBytes)
                return null;

            return DataUriPrefix + Convert.ToBase64String(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or SecurityException)
        {
            // Icon projection is optional metadata. A missing, racing, unreadable or malformed
            // file must never make the module snapshot or Web workspace unavailable.
            return null;
        }
    }

    private static bool ContainsReparsePoint(string moduleRoot, string candidate)
    {
        var root = moduleRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = new DirectoryInfo(Path.GetDirectoryName(candidate)!);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            current = current.Parent;
        }

        // The candidate was not proven to be below the expected root. Treat that as unsafe.
        return true;
    }
}

public sealed class WebModuleSnapshotProvider(IWebModuleSnapshotSource source, TimeProvider timeProvider)
{
    public WebModuleSnapshot Create() => new(timeProvider.GetUtcNow(), source.ReadModules());
}
