namespace QingToolbox.Abstractions.Modules;

public interface IModuleLifecycle : IAsyncDisposable
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default);
    Task OnActivateAsync(CancellationToken cancellationToken = default);
    Task OnDeactivateAsync(CancellationToken cancellationToken = default);
    Task OnUnloadAsync(CancellationToken cancellationToken = default);
}

/// <summary>A collectible in-process module that cannot export a plugin-defined UI object.</summary>
public interface IInProcessServiceModule : IModuleLifecycle;

/// <summary>
/// WPF view factory contract consumed only inside the trusted QingToolbox.ModuleHost process.
/// The Shell must never receive the returned object across the process boundary.
/// </summary>
public interface IModuleWpfViewFactory
{
    object? CreateView();
}
