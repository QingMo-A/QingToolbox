using System.Text.Json;
using System.IO;

namespace QingToolbox.Shell.WebShell;

public interface IWebCommandHandler
{
    string Command { get; }
    IReadOnlySet<string> AllowedPayloadProperties { get; }
    Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken);
}

public sealed class WebBridgeValidationException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }

public sealed class WebPingCommandHandler(TimeProvider timeProvider) : IWebCommandHandler
{
    public string Command => "app.ping";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>();
    public Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
        Task.FromResult<object>(new WebPingResponse(true, timeProvider.GetUtcNow()));
}

public sealed class WebSnapshotCommandHandler(WebAppSnapshotProvider snapshots) : IWebCommandHandler
{
    public string Command => "app.getSnapshot";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>();
    public Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken) => Task.FromResult<object>(snapshots.Create());
}

public sealed class WebReadyCommandHandler(WebAppSnapshotProvider snapshots, WebAssetIdentity assets) : IWebCommandHandler
{
    public string Command => "web.ready";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "assetBuildId", "documentReadyState", "transportMode" };
    public Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var ready = payload.Deserialize<WebReadyPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (ready is null || ready.AssetBuildId != assets.AssetBuildId || ready.DocumentReadyState != "complete" || ready.TransportMode != "WebView")
            throw new WebBridgeValidationException("InvalidReady", "The Web Shell readiness identity is invalid.");
        return Task.FromResult<object>(snapshots.Create());
    }
}

public sealed class WebAssetIdentity
{
    public WebAssetIdentity(string assetRoot)
    {
        var path = Path.Combine(assetRoot, "qing-web-assets.json");
        var manifest = JsonSerializer.Deserialize<WebAssetManifest>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The Web asset manifest is invalid.");
        AssetBuildId = manifest.AssetBuildId;
    }
    public string AssetBuildId { get; }
}
