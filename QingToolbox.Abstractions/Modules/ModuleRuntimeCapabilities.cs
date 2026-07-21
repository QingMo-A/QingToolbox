namespace QingToolbox.Abstractions.Modules;

public enum ModuleRuntimeIsolation
{
    LegacyInProcess = 0,
    InProcessCollectible = 1,
    OutOfProcess = 2
}

public enum ModuleUiKind { None = 0, Wpf = 1 }

public enum ModuleUpdateCapability { Unsupported = 0, RestartRequired = 1, LiveTransaction = 2 }

public sealed record ModuleRuntimeCapabilities(
    ModuleRuntimeIsolation RuntimeIsolation,
    ModuleUiKind UiKind,
    ModuleUpdateCapability UpdateCapability)
{
    public static ModuleRuntimeCapabilities Resolve(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.RuntimeIsolation is { } isolation && manifest.UiKind is { } ui)
        {
            var capability = isolation switch
            {
                ModuleRuntimeIsolation.InProcessCollectible when ui == ModuleUiKind.None =>
                    ModuleUpdateCapability.LiveTransaction,
                ModuleRuntimeIsolation.OutOfProcess when ui == ModuleUiKind.Wpf =>
                    ModuleUpdateCapability.LiveTransaction,
                ModuleRuntimeIsolation.LegacyInProcess => ModuleUpdateCapability.RestartRequired,
                _ => ModuleUpdateCapability.Unsupported
            };
            return new(isolation, ui, capability);
        }

        // Old manifests remain loadable, but their UI/runtime boundary is not sufficiently
        // attested for a live program-directory transaction.
        return new(ModuleRuntimeIsolation.LegacyInProcess, ModuleUiKind.Wpf,
            ModuleUpdateCapability.RestartRequired);
    }
}
