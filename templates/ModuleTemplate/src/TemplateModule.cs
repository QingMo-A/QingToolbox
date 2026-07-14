using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.Template;

public sealed class TemplateModule : IToolModule
{
    private ModuleContext? _context;

    public string Id => "qing.template";

    public string Name => "Template Module";

    public string Description => "A starter module template for QingToolbox.";

    public Task OnLoadAsync(
        ModuleContext context,
        CancellationToken cancellationToken = default)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task OnActivateAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task OnDeactivateAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        _context = null;
        return Task.CompletedTask;
    }

    public object? CreateView()
    {
        if (_context is null)
        {
            throw new InvalidOperationException("Module context is not available.");
        }

        return new TemplateView(_context.Localization, _context.ModuleId);
    }

    public ValueTask DisposeAsync()
    {
        _context = null;
        return ValueTask.CompletedTask;
    }
}
