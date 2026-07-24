using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using QingToolbox.Shell.Services;

namespace QingToolbox.Shell.WebShell;

public sealed class WebBridgeHost(WebBridgeDispatcher dispatcher, WebAppSnapshotProvider snapshots,
    WebNavigationPolicy navigation, SessionLogService log) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private CoreWebView2? _core;
    private CancellationTokenSource? _session;
    private long _generation;
    private bool _disposed;
    public event Action<long>? ReadyAccepted;
    public event Action<string, long>? CommandSucceeded;
    public long Generation { get { lock (_sync) return _generation; } }

    public long Attach(CoreWebView2 core)
    {
        lock (_sync)
        {
            DetachCore();
            _core = core;
            _session = new CancellationTokenSource();
            _generation++;
            core.WebMessageReceived += OnMessageReceived;
            return _generation;
        }
    }

    public void Detach() { lock (_sync) DetachCore(); }
    public void PublishHostEvent(string name) => PostCurrent(new WebBridgeEvent(WebBridgeProtocol.Version, "app.hostEvent", new { name }));
    public void PublishSnapshot() => PostCurrent(new WebBridgeEvent(WebBridgeProtocol.Version, "app.snapshot", snapshots.Create()));

    private async void OnMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        CoreWebView2 core; long generation; CancellationToken token;
        lock (_sync)
        {
            if (_disposed || sender is not CoreWebView2 captured || !ReferenceEquals(captured, _core) || _session is null) return;
            core = captured; generation = _generation; token = _session.Token;
        }
        if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source) || !navigation.IsAllowed(source))
        { log.Warning("WebShell", "Bridge request rejected; failure=UntrustedSource."); return; }
        var result = await dispatcher.DispatchAsync(args.WebMessageAsJson, token);
        if (!IsCurrent(core, generation, token)) return;
        Post(core, result.Response);
        if (result.Response.Success && result.ValidatedCommand is not null) CommandSucceeded?.Invoke(result.ValidatedCommand, generation);
        if (result.Response.Success && result.ValidatedCommand == "web.ready") ReadyAccepted?.Invoke(generation);
    }

    private bool IsCurrent(CoreWebView2 core, long generation, CancellationToken token)
    { lock (_sync) return !_disposed && !token.IsCancellationRequested && ReferenceEquals(core, _core) && generation == _generation; }

    private void PostCurrent(object message) { lock (_sync) { if (_core is not null) Post(_core, message); } }
    private void Post(CoreWebView2 core, object message)
    { try { core.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions)); } catch (InvalidOperationException) { log.Warning("WebShell", "Bridge post failed; failure=WebViewUnavailable."); } }

    private void DetachCore()
    {
        _session?.Cancel(); _session?.Dispose(); _session = null;
        if (_core is not null) _core.WebMessageReceived -= OnMessageReceived;
        _core = null;
    }
    public void Dispose() { lock (_sync) { _disposed = true; DetachCore(); } }
}
