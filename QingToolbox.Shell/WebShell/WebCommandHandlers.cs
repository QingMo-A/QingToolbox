using System.Text.Json;
using System.IO;

namespace QingToolbox.Shell.WebShell;

public interface IWebCommandHandler
{
    string Command { get; }
    IReadOnlySet<string> AllowedPayloadProperties { get; }
    Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken);
}

public sealed class WebBridgeValidationException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }

public sealed class WebPingCommandHandler(TimeProvider timeProvider, WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "app.ping";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string> { "activationNonce", "sessionToken" };
    public Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken)
    {
        var ping = payload.Deserialize<WebPingPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (ping is null || string.IsNullOrWhiteSpace(ping.ActivationNonce) == string.IsNullOrWhiteSpace(ping.SessionToken))
            throw new WebBridgeValidationException("InvalidPingCredential", "Exactly one ping credential is required.");
        var sessionToken = ping.ActivationNonce is not null
            ? activation.AcceptActivationPing(context.Generation, ping.ActivationNonce, context.SessionCancellation)
            : AcceptSession(context, ping.SessionToken!);
        return Task.FromResult<object>(new WebPingResponse(true, timeProvider.GetUtcNow(), sessionToken, true));
    }
    private string? AcceptSession(WebBridgeRequestContext context, string token)
    { activation.AcceptSessionPing(context.Generation, token, context.SessionCancellation); return null; }
}

public sealed class WebSnapshotCommandHandler(WebAppSnapshotProvider snapshots, WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "app.getSnapshot";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>();
    public Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken)
    { activation.RequireActivated(context.Generation, context.SessionCancellation); return Task.FromResult<object>(snapshots.Create()); }
}

public sealed class WebReadyCommandHandler(WebAppSnapshotProvider snapshots, Lazy<WebAssetIdentity> assets,
    WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "web.ready";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "assetBuildId", "documentReadyState", "transportMode" };
    public Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken)
    {
        var ready = payload.Deserialize<WebReadyPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (ready is null || ready.AssetBuildId != assets.Value.AssetBuildId || ready.DocumentReadyState != "complete" || ready.TransportMode != "WebView")
            throw new WebBridgeValidationException("InvalidReady", "The Web Shell readiness identity is invalid.");
        var challenge = activation.IssueChallenge(context.Generation, context.SessionCancellation);
        return Task.FromResult<object>(new WebReadyChallenge(challenge, snapshots.Create()));
    }
}

public sealed class WebActivationSession
{
    private readonly object _sync = new();
    private long _generation;
    private string? _nonce;
    private string? _sessionToken;
    private WebBridgeCommandPhase _phase = WebBridgeCommandPhase.Disposed;
    public void Begin(long generation)
    {
        lock (_sync) { _generation = generation; _nonce = RandomToken(); _sessionToken = null; _phase = WebBridgeCommandPhase.PreReady; }
    }
    public string IssueChallenge(long generation, CancellationToken session)
    { lock (_sync) { Validate(generation, session, WebBridgeCommandPhase.PreReady); _phase = WebBridgeCommandPhase.ChallengeIssued; return _nonce!; } }
    public string AcceptActivationPing(long generation, string nonce, CancellationToken session)
    { lock (_sync) { Validate(generation, session, WebBridgeCommandPhase.ChallengeIssued); if (!Matches(_nonce, nonce)) throw Invalid("InvalidActivationNonce"); _nonce = null; _sessionToken = RandomToken(); _phase = WebBridgeCommandPhase.Activated; return _sessionToken; } }
    public void AcceptSessionPing(long generation, string token, CancellationToken session)
    { lock (_sync) { Validate(generation, session, WebBridgeCommandPhase.Activated); if (!Matches(_sessionToken, token)) throw Invalid("InvalidSessionToken"); } }
    public void RequireActivated(long generation, CancellationToken session)
    { lock (_sync) Validate(generation, session, WebBridgeCommandPhase.Activated); }
    public bool IsActivated(long generation) { lock (_sync) return generation == _generation && _phase == WebBridgeCommandPhase.Activated; }
    public void Invalidate(long generation) { lock (_sync) { if (generation != _generation) return; _nonce = null; _sessionToken = null; _phase = WebBridgeCommandPhase.Disposed; } }
    private void Validate(long generation, CancellationToken session, WebBridgeCommandPhase required)
    { if (session.IsCancellationRequested) throw Invalid("BridgeSessionCancelled"); if (generation != _generation) throw Invalid("StaleBridgeGeneration"); if (_phase != required) throw Invalid(required == WebBridgeCommandPhase.Activated ? "BridgeNotActivated" : "InvalidBridgePhase"); }
    private static bool Matches(string? expected, string actual) => expected is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual));
    private static string RandomToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static WebBridgeValidationException Invalid(string code) => new(code, "The bridge session credential or phase is invalid.");
}
