namespace QingToolbox.Abstractions.Modules;

/// <summary>Legacy in-process module contract. Live transactions are not supported for this shape.</summary>
public interface IToolModule : IModuleLifecycle, IModuleWpfViewFactory
{
}
