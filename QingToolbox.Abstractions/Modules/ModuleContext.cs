using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Abstractions.Modules;

public sealed class ModuleContext
{
    public required string ModuleId { get; init; }

    public required string ModuleDirectory { get; init; }

    public required string DataDirectory { get; init; }

    public required ILocalizationService Localization { get; init; }

    public IReadOnlyDictionary<string, string> Properties { get; init; }
        = new Dictionary<string, string>();
}
