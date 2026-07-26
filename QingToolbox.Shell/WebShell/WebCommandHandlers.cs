using System.Text.Json;
using System.IO;
using QingToolbox.Core.Settings;

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

public sealed class WebModuleSnapshotCommandHandler(WebModuleSnapshotProvider snapshots, WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "modules.getSnapshot";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>();
    public Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken)
    {
        activation.RequireActivated(context.Generation, context.SessionCancellation);
        return Task.FromResult<object>(snapshots.Create());
    }
}

public abstract class WebModuleLifecycleCommandHandler(
    WebModuleSnapshotProvider snapshots,
    WebActivationSession activation) : IWebCommandHandler
{
    public abstract string Command { get; }
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>(StringComparer.Ordinal) { "moduleId" };

    protected abstract Task<WebModuleLifecycleResult> ExecuteAsync(string moduleId, CancellationToken cancellationToken);

    public async Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken)
    {
        activation.RequireActivated(context.Generation, context.SessionCancellation);
        if (!payload.TryGetProperty("moduleId", out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new WebBridgeValidationException("InvalidPayload", "A non-empty module ID is required.");

        var result = await ExecuteAsync(property.GetString()!, context.SessionCancellation);
        if (result == WebModuleLifecycleResult.Succeeded) return snapshots.Create();
        throw ErrorForResult(result);
    }

    internal static WebBridgeValidationException ErrorForResult(WebModuleLifecycleResult result) => result switch
        {
            WebModuleLifecycleResult.NotFound => new WebBridgeValidationException("ModuleNotFound", "The requested module was not found."),
            WebModuleLifecycleResult.Busy => new WebBridgeValidationException("ModuleBusy", "The requested module is busy."),
            WebModuleLifecycleResult.Unavailable => new WebBridgeValidationException("ModuleOperationUnavailable", "The requested module operation is not available."),
            WebModuleLifecycleResult.ExecutionBlocked => new WebBridgeValidationException("ModuleExecutionBlocked", "Module operations are blocked while recovery is pending."),
            _ => new WebBridgeValidationException("ModuleOperationFailed", "The host could not complete the module operation.")
        };
}

public sealed class WebModuleLoadCommandHandler(IWebModuleLifecycleOperations operations,
    WebModuleSnapshotProvider snapshots, WebActivationSession activation)
    : WebModuleLifecycleCommandHandler(snapshots, activation)
{
    public override string Command => "modules.load";
    protected override Task<WebModuleLifecycleResult> ExecuteAsync(string moduleId, CancellationToken cancellationToken) =>
        operations.LoadAsync(moduleId, cancellationToken);
}

public sealed class WebModuleActivateCommandHandler(IWebModuleLifecycleOperations operations,
    WebModuleSnapshotProvider snapshots, WebActivationSession activation)
    : WebModuleLifecycleCommandHandler(snapshots, activation)
{
    public override string Command => "modules.activate";
    protected override Task<WebModuleLifecycleResult> ExecuteAsync(string moduleId, CancellationToken cancellationToken) =>
        operations.ActivateAsync(moduleId, cancellationToken);
}

public sealed class WebModuleOpenCommandHandler(IWebModuleLifecycleOperations operations,
    WebModuleSnapshotProvider snapshots, WebActivationSession activation)
    : WebModuleLifecycleCommandHandler(snapshots, activation)
{
    public override string Command => "modules.open";
    protected override Task<WebModuleLifecycleResult> ExecuteAsync(string moduleId, CancellationToken cancellationToken) =>
        operations.OpenAsync(moduleId, cancellationToken);
}

public sealed class WebModuleDeactivateCommandHandler(IWebModuleLifecycleOperations operations,
    WebModuleSnapshotProvider snapshots, WebActivationSession activation)
    : WebModuleLifecycleCommandHandler(snapshots, activation)
{
    public override string Command => "modules.deactivate";
    protected override Task<WebModuleLifecycleResult> ExecuteAsync(string moduleId, CancellationToken cancellationToken) => operations.DeactivateAsync(moduleId, cancellationToken);
}

public sealed class WebModuleUnloadCommandHandler(IWebModuleLifecycleOperations operations,
    WebModuleSnapshotProvider snapshots, WebActivationSession activation)
    : WebModuleLifecycleCommandHandler(snapshots, activation)
{
    public override string Command => "modules.unload";
    protected override Task<WebModuleLifecycleResult> ExecuteAsync(string moduleId, CancellationToken cancellationToken) => operations.UnloadAsync(moduleId, cancellationToken);
}

public sealed class WebSetModuleStartupAuthorizationCommandHandler(
    IWebModuleStartupAuthorizationOperations operations,
    WebModuleSnapshotProvider snapshots,
    WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "modules.setStartupAuthorization";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "moduleId", "enabled" };

    public async Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken)
    {
        activation.RequireActivated(context.Generation, context.SessionCancellation);
        if (!payload.TryGetProperty("moduleId", out var moduleIdProperty) ||
            moduleIdProperty.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(moduleIdProperty.GetString()) ||
            !payload.TryGetProperty("enabled", out var enabledProperty) || enabledProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new WebBridgeValidationException("InvalidPayload", "A non-empty module ID and boolean enabled value are required.");

        var result = await operations.SetAsync(moduleIdProperty.GetString()!, enabledProperty.GetBoolean(), context.SessionCancellation);
        if (result == WebModuleLifecycleResult.Succeeded) return snapshots.Create();
        throw WebModuleLifecycleCommandHandler.ErrorForResult(result);
    }
}

public sealed class WebLogSnapshotCommandHandler(WebLogSnapshotProvider snapshots, WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "logs.getSnapshot";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>();
    public Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken)
    { activation.RequireActivated(context.Generation, context.SessionCancellation); return Task.FromResult<object>(snapshots.Create()); }
}

public sealed class WebSettingsSnapshotCommandHandler(WebSettingsSnapshotProvider snapshots, WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "settings.getSnapshot";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>();
    public Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken)
    { activation.RequireActivated(context.Generation, context.SessionCancellation); return Task.FromResult<object>(snapshots.Create()); }
}

public sealed class WebSetShowLogsInSidebarCommandHandler(IWebSettingsMutation mutation,
    WebSettingsSnapshotProvider snapshots, WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "settings.setShowLogsInSidebar";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string> { "showLogsInSidebar" };
    public async Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken)
    {
        activation.RequireActivated(context.Generation, context.SessionCancellation);
        if (!payload.TryGetProperty("showLogsInSidebar", out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new WebBridgeValidationException("InvalidPayload", "A Boolean logs visibility value is required.");
        var value = property.GetBoolean();
        if (mutation.ShowLogsInSidebar != value)
            await mutation.SetShowLogsInSidebarAsync(value, context.SessionCancellation);
        return snapshots.Create();
    }
}

public sealed class WebSetMainWindowCloseBehaviorCommandHandler(IWebSettingsMutation mutation,
    WebSettingsSnapshotProvider snapshots, WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "settings.setMainWindowCloseBehavior";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string> { "mainWindowCloseBehavior" };
    public async Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken)
    {
        activation.RequireActivated(context.Generation, context.SessionCancellation);
        if (!payload.TryGetProperty("mainWindowCloseBehavior", out var property) || property.ValueKind != JsonValueKind.String)
            throw new WebBridgeValidationException("InvalidPayload", "A valid main window close behavior is required.");
        var value = property.GetString() switch
        {
            "Ask" => MainWindowCloseBehavior.Ask,
            "MinimizeToNotificationArea" => MainWindowCloseBehavior.MinimizeToNotificationArea,
            "ExitApplication" => MainWindowCloseBehavior.ExitApplication,
            _ => throw new WebBridgeValidationException("InvalidPayload", "A valid main window close behavior is required.")
        };
        if (mutation.MainWindowCloseBehavior != value)
            await mutation.SetMainWindowCloseBehaviorAsync(value, context.SessionCancellation);
        return snapshots.Create();
    }
}

public sealed class WebSetStartupPresentationModeCommandHandler(IWebSettingsMutation mutation,
    WebSettingsSnapshotProvider snapshots, WebActivationSession activation) : IWebCommandHandler
{
    public string Command => "settings.setStartupPresentationMode";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string> { "startupPresentationMode" };
    public async Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken cancellationToken)
    {
        activation.RequireActivated(context.Generation, context.SessionCancellation);
        if (!payload.TryGetProperty("startupPresentationMode", out var property) || property.ValueKind != JsonValueKind.String)
            throw new WebBridgeValidationException("InvalidPayload", "A valid startup presentation mode is required.");
        var value = property.GetString() switch
        {
            "MainWindow" => StartupPresentationMode.MainWindow,
            "Minimized" => StartupPresentationMode.Minimized,
            "FloatingBadge" => StartupPresentationMode.FloatingBadge,
            _ => throw new WebBridgeValidationException("InvalidPayload", "A valid startup presentation mode is required.")
        };
        if (mutation.StartupPresentationMode != value)
            await mutation.SetStartupPresentationModeAsync(value, context.SessionCancellation);
        return snapshots.Create();
    }
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
