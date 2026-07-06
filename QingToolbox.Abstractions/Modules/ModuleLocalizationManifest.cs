namespace QingToolbox.Abstractions.Modules;

public sealed class ModuleLocalizationManifest
{
    public string? BasePath { get; init; }
    public IReadOnlyDictionary<string, string>? Resources { get; init; }
}
