namespace QingToolbox.Abstractions.Modules;

/// <summary>Optional module-to-host request channel for showing its existing window.</summary>
public interface IModuleHostWindowActionSource
{
    event EventHandler<ModuleHostWindowActionEventArgs>? HostWindowActionRequested;
}
