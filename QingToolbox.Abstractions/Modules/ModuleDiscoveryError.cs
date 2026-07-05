namespace QingToolbox.Abstractions.Modules;

public sealed class ModuleDiscoveryError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Path { get; init; }
}
