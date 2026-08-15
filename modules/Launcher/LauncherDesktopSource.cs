using System.IO;

namespace QingToolbox.Modules.Launcher;

public interface ILauncherDesktopSource
{
    IReadOnlyList<ResolvedLauncherItem> Scan();
}

public sealed class WindowsLauncherDesktopSource : ILauncherDesktopSource
{
    public IReadOnlyList<ResolvedLauncherItem> Scan()
    {
        var results = new List<ResolvedLauncherItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> paths;
            try { paths = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var path in paths)
            {
                if (!seenPaths.Add(path)) continue;
                var extension = Path.GetExtension(path);
                if ((!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                     !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                     !extension.Equals(".url", StringComparison.OrdinalIgnoreCase)) ||
                    !LauncherItemResolver.TryResolve(path, out var item) || item is null) continue;
                results.Add(item);
            }
        }
        return results;
    }
}
