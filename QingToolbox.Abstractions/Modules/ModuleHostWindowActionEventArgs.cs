namespace QingToolbox.Abstractions.Modules;

public sealed class ModuleHostWindowActionEventArgs(ModuleHostWindowAction action) : EventArgs
{
    public ModuleHostWindowAction Action { get; } = action;
}
