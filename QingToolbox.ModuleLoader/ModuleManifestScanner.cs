using QingToolbox.Abstractions.Modules;
using System.Text.Json;

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
            var code = exception is JsonException && HasUnknownRuntimeCapability(manifestPath)
                ? "Manifest.RuntimeCapabilityUnsupported"
                : "Manifest.ReadFailed";
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
                        Code = code,
                        Message = exception.Message,
                        Path = manifestPath
                    }
                ]
            };
        }
    }

    private static bool HasUnknownRuntimeCapability(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            return IsUnknown<ModuleRuntimeIsolation>(root, "runtimeIsolation") ||
                   IsUnknown<ModuleUiKind>(root, "uiKind");
        }
        catch { return false; }
    }

    private static bool IsUnknown<TEnum>(JsonElement root, string propertyName) where TEnum : struct, Enum =>
        root.TryGetProperty(propertyName, out var value) &&
        (value.ValueKind != JsonValueKind.String ||
         !Enum.TryParse<TEnum>(value.GetString(), true, out var parsed) ||
         !Enum.IsDefined(parsed));

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
