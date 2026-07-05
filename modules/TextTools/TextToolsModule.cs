using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.TextTools;

public sealed class TextToolsModule : IToolModule
{
    public string Id => "qing.texttools";

    public string Name => "Text Tools";

    public string Description => "Lightweight text conversion and formatting tools.";

    public Task OnLoadAsync(
        ModuleContext context,
        CancellationToken cancellationToken = default)
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
        return new TextToolsView();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
