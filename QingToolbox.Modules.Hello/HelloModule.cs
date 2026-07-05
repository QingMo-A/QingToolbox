using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.Hello;

public sealed class HelloModule : IToolModule
{
    public string Id => "qing.hello";

    public string Name => "Hello Module";

    public string Description => "A minimal test module for QingToolbox.";

    public Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task OnActivateAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task OnDeactivateAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public object? CreateView()
    {
        return "Hello from QingToolbox module.";
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
