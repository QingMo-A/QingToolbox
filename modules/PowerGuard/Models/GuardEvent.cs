namespace QingToolbox.Modules.PowerGuard.Models;
public sealed record GuardEvent(DateTimeOffset TimestampUtc, string Type, string? Detail = null);
