namespace QingToolbox.Abstractions.Modules;

/// <summary>Optional module-owned request for a non-standard host window presentation.</summary>
public interface IModuleHostWindowPresentationSource
{
    ModuleHostWindowPresentationMode HostWindowPresentationMode { get; }
}
