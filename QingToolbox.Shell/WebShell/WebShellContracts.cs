using System.Text.Json;

namespace QingToolbox.Shell.WebShell;

public static class WebBridgeProtocol { public const int Version = 1; }
public sealed record WebBridgeRequest(int ProtocolVersion, string? RequestId, string? Command, JsonElement Payload);
public sealed record WebBridgeError(string Code, string Message);
public sealed record WebBridgeResponse(int ProtocolVersion, string RequestId, bool Success, object Payload, WebBridgeError? Error);
public sealed record WebBridgeEvent(int ProtocolVersion, string Event, object Payload);
public sealed record WebAppSnapshot(string EnvironmentKind, string EnvironmentDisplayName, string HostVersion,
    int ProtocolVersion, int TotalModuleCount, int ValidModuleCount, int RunningModuleCount, DateTimeOffset GeneratedAt);
public sealed record WebPingResponse(bool Pong, DateTimeOffset HostTime);
