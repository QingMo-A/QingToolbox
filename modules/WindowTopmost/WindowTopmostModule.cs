using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.WindowTopmost;

public sealed class WindowTopmostModule : IToolModule
{
    public string Id => "qing.windowtopmost";
    public string Name => "Window Topmost";
    public string Description =>
        "Select a visible window and toggle whether it stays always on top.";
    public Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task OnActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task OnDeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task OnUnloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public object CreateView() => new WindowTopmostView();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
