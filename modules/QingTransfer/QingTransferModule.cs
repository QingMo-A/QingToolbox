using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.QingTransfer;

public sealed class QingTransferModule : IToolModule
{
    private readonly List<WeakReference<QingTransferView>> _views = [];
    private QingTransferDiscoveryService? _discovery;
    private QingTransferSession? _session;
    private ModuleContext? _context;
    private bool _disposed;

    public string Id => "qing.qingtransfer";
    public string Name => "QingTransfer";
    public string Description => "Transfer files between QingToolbox devices on the local network.";

    public Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        if (_context is not null) return Task.CompletedTask;
        _context = context;
        _discovery = new QingTransferDiscoveryService(Environment.MachineName, context.DataDirectory);
        _session = new QingTransferSession(_discovery, Environment.MachineName);
        context.Localization.CultureChanged += OnCultureChanged;
        return Task.CompletedTask;
    }

    public Task OnActivateAsync(CancellationToken cancellationToken = default) =>
        _discovery?.StartAsync(cancellationToken) ?? Task.CompletedTask;

    public Task OnDeactivateAsync(CancellationToken cancellationToken = default) =>
        _discovery?.StopAsync() ?? Task.CompletedTask;

    public async Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        var context = _context;
        if (context is not null) context.Localization.CultureChanged -= OnCultureChanged;
        foreach (var reference in _views)
            if (reference.TryGetTarget(out var view)) await view.DisposeAsync();
        _views.Clear();
        if (_session is not null) await _session.DisposeAsync();
        if (_discovery is not null)
        {
            _discovery.IncomingClientHandler = null;
            await _discovery.DisposeAsync();
        }
        _session = null;
        _discovery = null;
        _context = null;
    }

    public object CreateView()
    {
        var context = _context ?? throw new InvalidOperationException("Module context is not available.");
        var discovery = _discovery ?? throw new InvalidOperationException("Module is not loaded.");
        var session = _session ?? throw new InvalidOperationException("Module is not loaded.");
        var view = new QingTransferView(discovery, session, context.Localization, context.ModuleId);
        _views.Add(new(view));
        return view;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await OnUnloadAsync().ConfigureAwait(false);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var reference in _views)
            if (reference.TryGetTarget(out var view)) view.Dispatcher.BeginInvoke(view.RefreshLocalization);
    }
}
