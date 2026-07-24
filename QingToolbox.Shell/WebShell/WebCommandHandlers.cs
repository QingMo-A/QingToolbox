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

public sealed class WebPingCommandHandler(TimeProvider timeProvider, WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "app.ping";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string> { "activationNonce" };
    public Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var ping = payload.Deserialize<WebPingPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        activation.AcceptPing(ping?.ActivationNonce);
        return Task.FromResult<object>(new WebPingResponse(true, timeProvider.GetUtcNow()));
    }
}

public sealed class WebSnapshotCommandHandler(WebAppSnapshotProvider snapshots) : IWebCommandHandler
{
    public string Command => "app.getSnapshot";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>();
    public Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken) => Task.FromResult<object>(snapshots.Create());
}

public sealed class WebReadyCommandHandler(WebAppSnapshotProvider snapshots, Lazy<WebAssetIdentity> assets,
    WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "web.ready";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "assetBuildId", "documentReadyState", "transportMode" };
    public Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var ready = payload.Deserialize<WebReadyPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (ready is null || ready.AssetBuildId != assets.Value.AssetBuildId || ready.DocumentReadyState != "complete" || ready.TransportMode != "WebView")
            throw new WebBridgeValidationException("InvalidReady", "The Web Shell readiness identity is invalid.");
        var challenge = activation.IssueChallenge();
        return Task.FromResult<object>(new WebReadyChallenge(challenge, snapshots.Create()));
    }
}

public sealed class WebActivationSession
{
    private readonly object _sync = new();
    private long _generation;
    private string? _nonce;
    private bool _readyIssued;
    private bool _activated;
    public void Begin(long generation)
    {
        lock (_sync) { _generation = generation; _nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(); _readyIssued = false; _activated = false; }
    }
    public string IssueChallenge()
    { lock (_sync) { if (_nonce is null) throw new WebBridgeValidationException("ActivationUnavailable", "The activation session is unavailable."); _readyIssued = true; return _nonce; } }
    public void AcceptPing(string? nonce)
    { lock (_sync) { if (!_readyIssued || _activated || _nonce is null || string.IsNullOrWhiteSpace(nonce) || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(_nonce), System.Text.Encoding.UTF8.GetBytes(nonce))) throw new WebBridgeValidationException("InvalidActivationNonce", "The activation challenge is invalid."); _activated = true; _nonce = null; } }
    public bool IsActivated(long generation) { lock (_sync) return generation == _generation && _activated; }
    public void Invalidate() { lock (_sync) { _nonce = null; _readyIssued = false; _activated = false; } }
}
