using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using QingToolbox.Shell.Services;

namespace QingToolbox.Shell.WebShell;

public sealed class WebBridgeHost(WebBridgeDispatcher dispatcher, WebAppSnapshotProvider snapshots,
    WebNavigationPolicy navigation, SessionLogService log)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private CoreWebView2? _core;

    public void Attach(CoreWebView2 core)
    {
        Detach(); _core = core; core.WebMessageReceived += OnMessageReceived;
    }
    public void Detach() { if (_core is not null) _core.WebMessageReceived -= OnMessageReceived; _core = null; }
    public void PublishHostEvent(string name) => Post(new WebBridgeEvent(WebBridgeProtocol.Version, "app.hostEvent", new { name }));
    public void PublishSnapshot() => Post(new WebBridgeEvent(WebBridgeProtocol.Version, "app.snapshot", snapshots.Create()));
    private async void OnMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source) || !navigation.IsAllowed(source))
        {
            log.Warning("WebShell", "Bridge request rejected; failure=UntrustedSource.");
            return;
        }
        var response = await dispatcher.DispatchAsync(args.WebMessageAsJson);
        Post(response);
        if (response.Success) PublishHostEvent("bridge.requestCompleted");
        if (response.Success && args.WebMessageAsJson.Contains("web.ready", StringComparison.Ordinal)) PublishSnapshot();
    }
    private void Post(object message)
    {
        try { _core?.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions)); }
        catch (InvalidOperationException) { log.Warning("WebShell", "Bridge post failed; failure=WebViewUnavailable."); }
    }
}
