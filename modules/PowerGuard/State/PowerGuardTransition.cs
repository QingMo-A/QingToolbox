namespace QingToolbox.Modules.PowerGuard.State;
public sealed record PowerGuardTransition(PowerGuardState Previous, PowerGuardState Current, DateTimeOffset TimestampUtc);
