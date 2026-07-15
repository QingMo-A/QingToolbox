namespace QingToolbox.Modules.PowerGuard.State;

public sealed class PowerGuardStateMachine
{
    public PowerGuardState State { get; private set; } = PowerGuardState.Disabled;
    public DateTimeOffset StateSinceUtc { get; private set; } = DateTimeOffset.UtcNow;
    public PowerGuardTransition MoveTo(PowerGuardState next, DateTimeOffset now)
    {
        var transition = new PowerGuardTransition(State, next, now);
        State = next;
        StateSinceUtc = now;
        return transition;
    }
}
