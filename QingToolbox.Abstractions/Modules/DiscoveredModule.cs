namespace QingToolbox.Abstractions.Modules;

public sealed class DiscoveredModule
{
    public required ModuleManifest Manifest { get; init; }

    public required string ModuleDirectory { get; init; }

    public required string ManifestPath { get; init; }

    public ModuleState State { get; init; } = ModuleState.NotLoaded;

    public IReadOnlyList<ModuleDiscoveryError> Errors { get; init; }
        = Array.Empty<ModuleDiscoveryError>();

    public bool IsValid => Errors.Count == 0;
}
