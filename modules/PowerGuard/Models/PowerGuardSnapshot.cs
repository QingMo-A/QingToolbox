using QingToolbox.Modules.PowerGuard.State;

namespace QingToolbox.Modules.PowerGuard.Models;

public sealed record PowerGuardSnapshot(
    PowerGuardState State,
    bool GuardEnabled,
    bool IsOnline,
    DateTimeOffset? LastOnlineUtc,
    int RemainingSeconds,
    bool IsSuppressed,
    string? Error = null);
