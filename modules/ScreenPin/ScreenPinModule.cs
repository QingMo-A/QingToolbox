using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.ScreenPin;

public sealed class ScreenPinModule : IToolModule
{
    private readonly ScreenPinManager _manager = new();
    private ModuleContext? _context;

    public string Id => "qing.screenpin";
    public string Name => "Screen Pin";
    public string Description =>
        "Capture a screen region and pin it as a resizable floating image.";

    public Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _manager.ConfigureLocalization(context.Localization, context.ModuleId);
        return Task.CompletedTask;
    }
    public Task OnActivateAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task OnDeactivateAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        _context = null;
        await _manager.CloseAllAsync();
    }

    public object CreateView() => _context is null
        ? throw new InvalidOperationException("Module context is not available.")
        : new ScreenPinView(_manager, _context.Localization, _context.ModuleId);

    public async ValueTask DisposeAsync()
    {
        _context = null;
        await _manager.CloseAllAsync();
    }
}
