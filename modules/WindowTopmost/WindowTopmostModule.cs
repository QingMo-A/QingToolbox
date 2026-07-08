using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.WindowTopmost;

public sealed class WindowTopmostModule : IToolModule
{
    private ModuleContext? _context;
    public string Id => "qing.windowtopmost";
    public string Name => "Window Topmost";
    public string Description =>
        "Select a visible window and toggle whether it stays always on top.";
    public Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        return Task.CompletedTask;
    }
    public Task OnActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task OnDeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        _context = null;
        return Task.CompletedTask;
    }
    public object CreateView() => _context is null
        ? throw new InvalidOperationException("Module context is not available.")
        : new WindowTopmostView(_context.Localization, _context.ModuleId);
    public ValueTask DisposeAsync()
    {
        _context = null;
        return ValueTask.CompletedTask;
    }
}
