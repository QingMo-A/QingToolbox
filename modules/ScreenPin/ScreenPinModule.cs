using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.ScreenPin;

public sealed class ScreenPinModule : IToolModule
{
    private readonly ScreenPinManager _manager = new();

    public string Id => "qing.screenpin";
    public string Name => "Screen Pin";
    public string Description =>
        "Capture a screen region and pin it as a resizable floating image.";

    public Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task OnActivateAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task OnDeactivateAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task OnUnloadAsync(CancellationToken cancellationToken = default) =>
        await _manager.CloseAllAsync();

    public object CreateView() => new ScreenPinView(_manager);

    public async ValueTask DisposeAsync() => await _manager.CloseAllAsync();
}
