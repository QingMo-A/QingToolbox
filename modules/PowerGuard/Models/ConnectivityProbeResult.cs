namespace QingToolbox.Modules.PowerGuard.Models;

public sealed record ProbeEndpointResult(string Name, bool Succeeded, long ElapsedMilliseconds, string FailureCategory);
public sealed record ConnectivityProbeResult(bool IsOnline, bool NetworkInterfaceAvailable, IReadOnlyList<ProbeEndpointResult> Endpoints, DateTimeOffset TimestampUtc);
