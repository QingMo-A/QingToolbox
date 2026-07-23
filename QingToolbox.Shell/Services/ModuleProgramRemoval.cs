using System.IO;
using QingToolbox.Core.Settings;

namespace QingToolbox.Shell.Services;

internal enum ModuleProgramRemovalStatus
{
    Completed,
    ProgramDeletionFailed,
    AuthorizationCleanupFailed
}

internal sealed record ModuleProgramRemovalResult(
    bool ProgramDeleted,
    bool StartupAuthorizationRemoved,
    ModuleProgramRemovalStatus Status,
    string? FailureCode);

internal static class ModuleProgramRemoval
{
    public static Task<ModuleProgramRemovalResult> DeleteAsync(
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

        return DeleteCoreAsync(
            () =>
            {
                Directory.Delete(fullDirectory, recursive: true);
                return Task.CompletedTask;
            },
            () => settingsService.UpdateAsync(settings =>
                settings.StartupModules.RemoveAll(item => item.ModuleId == moduleId), cancellationToken));
    }

    internal static async Task<ModuleProgramRemovalResult> DeleteCoreAsync(
        Func<Task> deleteProgram,
        Func<Task> removeStartupAuthorization)
    {
        try
        {
            await deleteProgram();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(false, false, ModuleProgramRemovalStatus.ProgramDeletionFailed,
                $"ModuleRemoval.ProgramDeletion.{exception.GetType().Name}");
        }

        try
        {
            await removeStartupAuthorization();
            return new(true, true, ModuleProgramRemovalStatus.Completed, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(true, false, ModuleProgramRemovalStatus.AuthorizationCleanupFailed,
                $"ModuleRemoval.AuthorizationCleanup.{exception.GetType().Name}");
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
