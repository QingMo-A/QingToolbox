namespace QingToolbox.Modules.PowerGuard.Models;

internal sealed record CountdownTiming(
    Guid SessionId,
    long StartedTimestamp,
    TimeSpan Duration,
    TimeSpan Extension,
    DateTimeOffset StartedUtc);
