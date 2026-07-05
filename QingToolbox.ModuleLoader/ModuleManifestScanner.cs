using QingToolbox.Abstractions.Modules;

namespace QingToolbox.ModuleLoader;

public sealed class ModuleManifestScanner(
    ModuleManifestReader reader,
    ModuleManifestValidator validator)
{
    public async Task<IReadOnlyList<DiscoveredModule>> ScanAsync(
        string modulesRootDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(modulesRootDirectory))
        {
            return Array.Empty<DiscoveredModule>();
        }

        var discoveredModules = new List<DiscoveredModule>();
        foreach (var moduleDirectory in Directory.EnumerateDirectories(modulesRootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(moduleDirectory, "module.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            discoveredModules.Add(await DiscoverAsync(moduleDirectory, manifestPath, cancellationToken));
        }

        return discoveredModules;
    }

    private async Task<DiscoveredModule> DiscoverAsync(
        string moduleDirectory,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await reader.ReadAsync(manifestPath, cancellationToken);
            var errors = validator.Validate(manifest, moduleDirectory, manifestPath);
            return new DiscoveredModule
            {
                Manifest = manifest ?? CreateFallbackManifest(moduleDirectory),
                ModuleDirectory = moduleDirectory,
                ManifestPath = manifestPath,
                State = errors.Count == 0 ? ModuleState.NotLoaded : ModuleState.Failed,
                Errors = errors
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DiscoveredModule
            {
                Manifest = CreateFallbackManifest(moduleDirectory),
                ModuleDirectory = moduleDirectory,
                ManifestPath = manifestPath,
                State = ModuleState.Failed,
                Errors =
                [
                    new ModuleDiscoveryError
                    {
                        Code = "Manifest.ReadFailed",
                        Message = exception.Message,
                        Path = manifestPath
                    }
                ]
            };
        }
    }

    private static ModuleManifest CreateFallbackManifest(string moduleDirectory)
    {
        var directoryName = Path.GetFileName(moduleDirectory);
        return new ModuleManifest
        {
            Id = directoryName,
            Name = directoryName,
            Version = "0.0.0",
            Entry = string.Empty
        };
    }
}
