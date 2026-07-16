using QingToolbox.Modules.PowerGuard.State;

namespace QingToolbox.Modules.PowerGuard.Models;

public sealed record PowerGuardSnapshot(
    PowerGuardState State,
    bool GuardEnabled,
    bool IsOnline,
    DateTimeOffset? LastOnlineUtc,
    DateTimeOffset? LastProbeUtc,
    DateTimeOffset? LastSuccessfulProbeUtc,
    int ConsecutiveProbeFailures,
    int RemainingSeconds,
    bool IsSuppressed,
    string? Error = null)
{
    public bool CanSuppressCurrentOutage => State == PowerGuardState.Countdown;
    public bool CanExtendCountdown => State == PowerGuardState.Countdown && RemainingSeconds >= 0;
    public bool CanRearmCurrentOutage => State == PowerGuardState.SuppressedForCurrentOutage;
    public bool CanRequestShutdownNow => State is PowerGuardState.Countdown or PowerGuardState.ActionFailed;
    public bool CanChangeSettings => State is not (PowerGuardState.Stopping or PowerGuardState.ExecutingShutdown);
    public bool CanTestWarning => State is not (PowerGuardState.Countdown or PowerGuardState.ExecutingShutdown or PowerGuardState.Stopping);
    public bool CanProbeNow => State is not (PowerGuardState.Stopping or PowerGuardState.ExecutingShutdown);
}
