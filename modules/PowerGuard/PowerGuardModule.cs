using System.IO;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Modules.PowerGuard.Services;
using QingToolbox.Modules.PowerGuard.ViewModels;
using QingToolbox.Modules.PowerGuard.Views;

namespace QingToolbox.Modules.PowerGuard;

public sealed class PowerGuardModule : IToolModule
{
    private ModuleContext? _context;
    private ConnectivityProbeService? _probe;
    private PowerGuardSettingsStore? _settingsStore;
    private PowerGuardEventStore? _eventStore;
    private WarningWindowPresenter? _warning;
    private PowerGuardController? _controller;
    private readonly List<WeakReference<PowerGuardViewModel>> _views = [];
    private bool _disposed;
    public string Id => "qing.powerguard";
    public string Name => "PowerGuard";
    public string Description => "Monitor internet connectivity and safely shut down the computer after a confirmed outage.";

    public async Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        if (_context is not null) return;
        _context = context; Directory.CreateDirectory(context.DataDirectory);
        _settingsStore = new(context.DataDirectory); _eventStore = new(context.DataDirectory); _probe = new();
        var settings = await _settingsStore.ReadAsync(cancellationToken);
        _warning = new(context.Localization, context.ModuleId);
        _controller = new(_probe, new PowerActionService(), _settingsStore, _eventStore, _warning, settings);
        _warning.Configure(() => _controller.SuppressCurrentOutageAsync(), () => _controller.ExtendCountdownAsync(), () => _controller.ShutdownNowAsync());
        context.Localization.CultureChanged += OnCultureChanged;
    }
    public Task OnActivateAsync(CancellationToken cancellationToken = default) => Controller.ActivateAsync(cancellationToken);
    public Task OnDeactivateAsync(CancellationToken cancellationToken = default) => _controller?.DeactivateAsync(CancellationToken.None) ?? Task.CompletedTask;
    public async Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        if (_context is null) return;
        _context.Localization.CultureChanged -= OnCultureChanged;
        if (_controller is not null) await _controller.DeactivateAsync(CancellationToken.None);
        foreach (var weak in _views) if (weak.TryGetTarget(out var view)) view.Dispose();
        _views.Clear();
        if (_controller is not null) await _controller.DisposeAsync();
        if (_warning is not null) await _warning.DisposeAsync();
        _probe?.Dispose(); _settingsStore?.Dispose(); _eventStore?.Dispose();
        _controller=null;_warning=null;_probe=null;_settingsStore=null;_eventStore=null;_context=null;
    }
    public object CreateView()
    {
        var context=_context ?? throw new InvalidOperationException("Module context is not available.");
        var viewModel=new PowerGuardViewModel(Controller,context.Localization,context.ModuleId); _views.Add(new(viewModel));
        return new PowerGuardView(context.Localization,context.ModuleId,viewModel);
    }
    private PowerGuardController Controller => _controller ?? throw new InvalidOperationException("PowerGuard is not loaded.");
    private void OnCultureChanged(object? sender,EventArgs e)=>_warning?.RefreshLocalization();
    public async ValueTask DisposeAsync(){if(_disposed)return;_disposed=true;await OnUnloadAsync();}
}
