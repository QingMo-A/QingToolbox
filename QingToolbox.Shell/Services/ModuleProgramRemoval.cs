using System.IO;
using QingToolbox.Core.Settings;

namespace QingToolbox.Shell.Services;

internal static class ModuleProgramRemoval
{
    public static async Task DeleteAsync(
        string moduleId,
        string moduleDirectory,
        string userModulesDirectory,
        UserSettingsService settingsService,
        CancellationToken cancellationToken = default)
    {
        var fullDirectory = Path.GetFullPath(moduleDirectory);
        if (!IsDirectChildOf(fullDirectory, userModulesDirectory))
            throw new InvalidOperationException("The module directory is outside the user module root.");
        EnsureTreeContainsNoLinks(fullDirectory);

        var priorSettings = await settingsService.ReadAsync(cancellationToken);
        var priorAuthorizations = priorSettings.StartupModules
            .Where(item => item.ModuleId == moduleId)
            .ToArray();
        await settingsService.UpdateAsync(settings =>
            settings.StartupModules.RemoveAll(item => item.ModuleId == moduleId), cancellationToken);
        try
        {
            Directory.Delete(fullDirectory, recursive: true);
        }
        catch
        {
            await settingsService.UpdateAsync(settings =>
            {
                settings.StartupModules.RemoveAll(item => item.ModuleId == moduleId);
                settings.StartupModules.AddRange(priorAuthorizations);
            }, CancellationToken.None);
            throw;
        }
    }

    internal static bool IsDirectChildOf(string candidate, string parent)
    {
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(Path.GetDirectoryName(fullCandidate), fullParent, StringComparison.OrdinalIgnoreCase);
    }

    internal static void EnsureTreeContainsNoLinks(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Module links and reparse points cannot be removed automatically.");
    }
}
