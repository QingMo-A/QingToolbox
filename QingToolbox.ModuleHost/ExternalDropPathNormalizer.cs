using System.IO;

namespace QingToolbox.ModuleHost;

internal static class ExternalDropPathNormalizer
{
    private const int MaximumPaths = 16;

    internal static IReadOnlyList<string> Normalize(IEnumerable<string>? paths)
    {
        if (paths is null) return [];
        var result = new List<string>(MaximumPaths);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            if (result.Count == MaximumPaths || string.IsNullOrWhiteSpace(raw)) continue;
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(raw);
            }
            catch (ArgumentException) { continue; }
            catch (NotSupportedException) { continue; }
            if (!Path.IsPathFullyQualified(fullPath) || !seen.Add(fullPath)) continue;
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) continue;
            result.Add(fullPath);
        }
        return result;
    }
}
