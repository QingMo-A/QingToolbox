using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.TextTools;

public sealed class TextToolsModule : IToolModule
{
    private ModuleContext? _context;
    private readonly List<WeakReference<TextToolsView>> _views = [];
    public string Id => "qing.texttools";

    public string Name => "Text Tools";

    public string Description => "Lightweight text conversion and formatting tools.";

    public Task OnLoadAsync(
        ModuleContext context,
        CancellationToken cancellationToken = default)
    {
        _context = context;
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

    public async Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var reference in _views) if (reference.TryGetTarget(out var view)) await view.DisposeAsync();
        _views.Clear();
        _context = null;
    }

    public object? CreateView()
    {
        if (_context is null)
            throw new InvalidOperationException("Module context is not available.");
        var view = new TextToolsView(_context.Localization, _context.ModuleId);
        _views.Add(new(view));
        return view;
    }

    public async ValueTask DisposeAsync()
    {
        await OnUnloadAsync();
    }
}
