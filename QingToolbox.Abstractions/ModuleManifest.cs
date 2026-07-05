namespace QingToolbox.Abstractions;

public sealed record ModuleManifest(string Id, string Name, string Version, string EntryAssembly, string EntryType);
