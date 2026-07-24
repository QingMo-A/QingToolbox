using System.Text.Json;

namespace QingToolbox.Shell.WebShell;

public static class WebBridgeProtocol
{
    public const int Version = 3;
    public const int MaximumRequestBytes = 64 * 1024;
}

public sealed record WebBridgeRequest(int ProtocolVersion, string? RequestId, string? Command, JsonElement Payload);
public sealed record WebBridgeError(string Code, string Message);
public sealed record WebBridgeResponse(int ProtocolVersion, string RequestId, bool Success, object Payload, WebBridgeError? Error);
public sealed record WebBridgeEvent(int ProtocolVersion, string Event, object Payload);
public sealed record WebReadyPayload(string? AssetBuildId, string? DocumentReadyState, string? TransportMode);
public sealed record WebPingPayload(string? ActivationNonce);
public sealed record WebReadyChallenge(string ActivationNonce, WebAppSnapshot Snapshot);
public sealed record WebAppSnapshot(string EnvironmentKind, string EnvironmentDisplayName, string HostVersion,
    int ProtocolVersion, int TotalModuleCount, int ValidModuleCount, int RunningModuleCount, DateTimeOffset GeneratedAt);
public sealed record WebPingResponse(bool Pong, DateTimeOffset HostTime);
public sealed record WebAssetManifest(int SchemaVersion, string AssetBuildId, string PackageLockSha256,
    string SourceTreeSha256, IReadOnlyList<WebAssetFile> OutputFiles);
public sealed record WebAssetFile(string Path, long Size, string Sha256);
public sealed record WebBridgeDispatchResult(string? ValidatedCommand, string RequestId, int ProtocolVersion,
    JsonElement? TypedPayload, WebBridgeResponse Response);
public sealed record WebShellActivationFacts(bool NavigationSucceeded, bool ReadyIdentitySucceeded,
    bool SnapshotIssued, bool ActivationPingSucceeded, bool WorkspaceActivated);
