namespace QingToolbox.Abstractions.Modules;

public interface IToolModule : IAsyncDisposable
{
    string Id { get; }

    string Name { get; }

    string Description { get; }

    Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default);

    Task OnActivateAsync(CancellationToken cancellationToken = default);

    Task OnDeactivateAsync(CancellationToken cancellationToken = default);

    Task OnUnloadAsync(CancellationToken cancellationToken = default);

    object? CreateView();
}
