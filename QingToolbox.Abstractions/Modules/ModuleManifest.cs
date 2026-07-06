namespace QingToolbox.Abstractions.Modules;

public sealed class ModuleManifest
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Version { get; init; }

    public string? Author { get; init; }

    public required string Entry { get; init; }

    public string? Icon { get; init; }

    public ModuleRuntimeType RuntimeType { get; init; } = ModuleRuntimeType.InProcess;

    public ModuleLoadMode LoadMode { get; init; } = ModuleLoadMode.Manual;

    public IReadOnlyList<ModulePermission> Permissions { get; init; }
        = Array.Empty<ModulePermission>();

    public string? MinimumHostVersion { get; init; }

    public string? DefaultLanguage { get; init; }

    public ModuleLocalizationManifest? Localization { get; init; }
}
