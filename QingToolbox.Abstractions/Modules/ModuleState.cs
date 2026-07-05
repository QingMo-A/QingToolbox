namespace QingToolbox.Abstractions.Modules;

public enum ModuleState
{
    NotInstalled = 0,
    Installed = 1,
    NotLoaded = 2,
    Loading = 3,
    Loaded = 4,
    Activating = 5,
    Running = 6,
    Deactivating = 7,
    Deactivated = 8,
    Unloading = 9,
    Unloaded = 10,
    Failed = 11
}
