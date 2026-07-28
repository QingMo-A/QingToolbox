using System.Text.Json;

namespace QingToolbox.Shell.WebShell;

public static class WebBridgeProtocol
{
    public const int Version = 4;
    public const int MaximumRequestBytes = 64 * 1024;
}

public sealed record WebBridgeRequest(int ProtocolVersion, string? RequestId, string? Command, JsonElement Payload);
public sealed record WebBridgeError(string Code, string Message);
public sealed record WebBridgeResponse(int ProtocolVersion, string RequestId, bool Success, object Payload, WebBridgeError? Error);
public sealed record WebBridgeEvent(int ProtocolVersion, string Event, object Payload);
public sealed record WebReadyPayload(string? AssetBuildId, string? DocumentReadyState, string? TransportMode);
public sealed record WebPingPayload(string? ActivationNonce, string? SessionToken);
public sealed record WebReadyChallenge(string ActivationNonce, WebAppSnapshot Snapshot);
public sealed record WebAppSnapshot(string EnvironmentKind, string EnvironmentDisplayName, string HostVersion,
    int ProtocolVersion, int TotalModuleCount, int ValidModuleCount, int RunningModuleCount, DateTimeOffset GeneratedAt);
public sealed record WebPingResponse(bool Pong, DateTimeOffset HostTime, string? SessionToken, bool Activated);
public sealed record WebModuleSnapshot(DateTimeOffset GeneratedAt, IReadOnlyList<WebModuleSnapshotItem> Modules);
public sealed record WebModuleImportResponse(string Disposition, string? ImportedModuleId, WebModuleSnapshot Snapshot);
public sealed record WebModuleManagementResponse(string Disposition, WebModuleSnapshot Snapshot);
public sealed record WebModuleSnapshotItem(string Id, string DisplayName, string DisplayDescription, string Version,
    string Author, string RuntimeType, string LoadMode, string RuntimeState, bool IsValid, int ErrorCount,
    IReadOnlyList<string> Errors, IReadOnlyList<string> Permissions, string MinimumHostVersion, bool IsUserInstalled, bool CanRemove,
    bool CanLoad, bool CanActivate, bool CanOpen, bool CanDeactivate, bool CanUnload, bool IsBusy, bool IsExecutionBlocked,
    bool IsStartupEnabled, string StartupAuthorizationState, bool CanChangeStartupAuthorization, bool IsStartupAuthorizationBusy);
public sealed record WebLogSnapshot(DateTimeOffset GeneratedAt, IReadOnlyList<WebLogSnapshotEntry> Entries);
public sealed record WebLogSnapshotEntry(DateTimeOffset Timestamp, string Level, string Category, string Message);
public sealed record WebSettingsSnapshot(DateTimeOffset GeneratedAt, WebSettingsLanguage Language,
    bool ShowLogsInSidebar, string MainWindowCloseBehavior, string CloseBehaviorMessage,
    bool LaunchAtLogin, bool CanConfigureLaunchAtLogin, bool CanRepairStartup, string StartupPresentationMode,
    string StartupBackend, string StartupStatus, string StartupMessage);
public sealed record WebSettingsLanguage(string Code, string EffectiveCode, string DisplayName,
    IReadOnlyList<WebSettingsLanguageOption> Options);
public sealed record WebSettingsLanguageOption(string Code, string DisplayName, string NativeName);
public sealed record WebAssetManifest(int SchemaVersion, string AssetBuildId, string PackageLockSha256,
    string SourceTreeSha256, IReadOnlyList<WebAssetFile> OutputFiles);
public sealed record WebAssetFile(string Path, long Size, string Sha256);
public sealed record WebBridgeDispatchResult(string? ValidatedCommand, string RequestId, int ProtocolVersion,
    JsonElement? TypedPayload, WebBridgeResponse Response);
public sealed record WebShellActivationFacts(bool NavigationSucceeded, bool ReadyIdentitySucceeded,
    bool SnapshotIssued, bool ActivationPingSucceeded, bool WorkspaceActivated);
public sealed record WebBridgeRequestContext(long Generation, CancellationToken SessionCancellation);
public enum WebBridgeCommandPhase { PreReady, ChallengeIssued, Activated, Disposed }
