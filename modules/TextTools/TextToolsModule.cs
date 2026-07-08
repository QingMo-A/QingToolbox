using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.TextTools;

public sealed class TextToolsModule : IToolModule
{
    private ModuleContext? _context;
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

    public Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        _context = null;
        return Task.CompletedTask;
    }

    public object? CreateView()
    {
        return _context is null
            ? throw new InvalidOperationException("Module context is not available.")
            : new TextToolsView(_context.Localization, _context.ModuleId);
    }

    public ValueTask DisposeAsync()
    {
        _context = null;
        return ValueTask.CompletedTask;
    }
}
