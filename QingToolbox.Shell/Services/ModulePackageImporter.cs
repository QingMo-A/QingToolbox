using System.IO;
using System.IO.Compression;
using QingToolbox.ModuleLoader;

namespace QingToolbox.Shell.Services;

public sealed class ModulePackageImporter(
    ModuleManifestReader manifestReader,
    ModuleManifestValidator manifestValidator)
{
    private const int MaximumEntryCount = 2048;
    private const long MaximumExpandedSize = 256L * 1024 * 1024;

    public async Task<string> ImportAsync(
        string packagePath,
        string modulesRoot,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(packagePath), ".qmod", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected file is not a .qmod package.");
        }

        Directory.CreateDirectory(modulesRoot);
        var stagingDirectory = Path.Combine(
            modulesRoot,
            $".import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumEntryCount)
            {
                throw new InvalidDataException("The module package has an invalid number of entries.");
            }

            var expandedSize = archive.Entries.Sum(entry => entry.Length);
            if (expandedSize > MaximumExpandedSize)
            {
                throw new InvalidDataException("The expanded module package is larger than 256 MB.");
            }

            var manifestEntries = archive.Entries
                .Where(entry => string.Equals(
                    Path.GetFileName(NormalizeEntryPath(entry.FullName)),
                    "module.json",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (manifestEntries.Length != 1)
            {
                throw new InvalidDataException("A .qmod package must contain exactly one module.json.");
            }

            var normalizedManifestPath = NormalizeEntryPath(manifestEntries[0].FullName);
            var packagePrefix = normalizedManifestPath[..^"module.json".Length];

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedPath = NormalizeEntryPath(entry.FullName);
                if (!normalizedPath.StartsWith(packagePrefix, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Package entries must share the module.json root directory.");
                }

                var relativePath = normalizedPath[packagePrefix.Length..];
                if (string.IsNullOrEmpty(relativePath))
                {
                    continue;
                }

                var destinationPath = GetSafeDestinationPath(stagingDirectory, relativePath);
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await using var source = entry.Open();
                await using var destination = File.Create(destinationPath);
                await source.CopyToAsync(destination, cancellationToken);
            }

            var manifestPath = Path.Combine(stagingDirectory, "module.json");
            var manifest = await manifestReader.ReadAsync(manifestPath, cancellationToken);
            var validationErrors = manifestValidator.Validate(
                manifest,
                stagingDirectory,
                manifestPath);
            if (validationErrors.Count > 0)
            {
                throw new InvalidDataException(string.Join(
                    Environment.NewLine,
                    validationErrors.Select(error => error.Message)));
            }

            var moduleId = manifest!.Id;
            var folderName = CreateSafeFolderName(moduleId);
            var targetDirectory = Path.Combine(modulesRoot, folderName);
            if (Directory.Exists(targetDirectory))
            {
                throw new IOException($"Module '{moduleId}' is already installed.");
            }

            Directory.Move(stagingDirectory, targetDirectory);
            return moduleId;
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            throw;
        }
    }

    private static string NormalizeEntryPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(path) ||
            segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
        {
            throw new InvalidDataException($"Unsafe package path: {path}");
        }

        return string.Join('/', segments) + (path.EndsWith('/') ? "/" : string.Empty);
    }

    private static string GetSafeDestinationPath(string root, string relativePath)
    {
        var destination = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Package path escapes the module directory: {relativePath}");
        }

        return destination;
    }

    private static string CreateSafeFolderName(string moduleId)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var folderName = new string(moduleId
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
        if (string.IsNullOrWhiteSpace(folderName) || folderName is "." or "..")
        {
            throw new InvalidDataException("The module id cannot be used as an installation directory.");
        }

        return folderName;
    }
}
