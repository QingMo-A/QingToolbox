using QingToolbox.Abstractions.Modules;

namespace QingToolbox.ModuleLoader;

public sealed class ModuleManifestValidator
{
    public IReadOnlyList<ModuleDiscoveryError> Validate(
        ModuleManifest? manifest,
        string moduleDirectory,
        string manifestPath)
    {
        var errors = new List<ModuleDiscoveryError>();
        if (manifest is null)
        {
            errors.Add(CreateError("Manifest.Null", "The module manifest is null.", manifestPath));
            return errors;
        }

        AddRequiredError(errors, manifest.Id, "Manifest.MissingId", "Id", manifestPath);
        AddRequiredError(errors, manifest.Name, "Manifest.MissingName", "Name", manifestPath);
        AddRequiredError(errors, manifest.Version, "Manifest.MissingVersion", "Version", manifestPath);
        AddRequiredError(errors, manifest.Entry, "Manifest.MissingEntry", "Entry", manifestPath);

        ValidateRuntimeCapabilities(errors, manifest, manifestPath);

        if (!string.IsNullOrWhiteSpace(manifest.Entry))
        {
            var entryPath = Path.Combine(moduleDirectory, manifest.Entry);
            if (!File.Exists(entryPath))
            {
                errors.Add(CreateError(
                    "Manifest.EntryNotFound",
                    $"The module entry file '{manifest.Entry}' does not exist.",
                    entryPath));
            }
        }

        return errors;
    }

    private static void ValidateRuntimeCapabilities(
        ICollection<ModuleDiscoveryError> errors,
        ModuleManifest manifest,
        string manifestPath)
    {
        if (manifest.RuntimeIsolation is null && manifest.UiKind is null)
        {
            return;
        }

        if (manifest.RuntimeIsolation is null || manifest.UiKind is null)
        {
            errors.Add(CreateError(
                "Manifest.RuntimeCapabilityIncomplete",
                "The runtimeIsolation and uiKind fields must be declared together.",
                manifestPath));
            return;
        }

        var supported = (manifest.RuntimeIsolation, manifest.UiKind) switch
        {
            (ModuleRuntimeIsolation.LegacyInProcess, ModuleUiKind.Wpf) => true,
            (ModuleRuntimeIsolation.InProcessCollectible, ModuleUiKind.None) => true,
            (ModuleRuntimeIsolation.OutOfProcess, ModuleUiKind.Wpf) => true,
            _ => false
        };
        if (!supported)
        {
            errors.Add(CreateError(
                "Manifest.RuntimeCapabilityUnsupported",
                $"The runtime capability combination '{manifest.RuntimeIsolation} + {manifest.UiKind}' is unsupported.",
                manifestPath));
        }
    }

    private static void AddRequiredError(
        ICollection<ModuleDiscoveryError> errors,
        string? value,
        string code,
        string fieldName,
        string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(CreateError(code, $"The required field '{fieldName}' is missing.", manifestPath));
        }
    }

    private static ModuleDiscoveryError CreateError(string code, string message, string path) =>
        new() { Code = code, Message = message, Path = path };
}
