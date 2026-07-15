namespace QingToolbox.Modules.PowerGuard.State;

public sealed class PowerGuardStateMachine
{
    private static readonly IReadOnlyDictionary<PowerGuardState, PowerGuardState[]> Allowed = new Dictionary<PowerGuardState, PowerGuardState[]>
    {
        [PowerGuardState.Disabled]=[PowerGuardState.StartupGrace,PowerGuardState.Stopping],
        [PowerGuardState.StartupGrace]=[PowerGuardState.Online,PowerGuardState.SuspectedOffline,PowerGuardState.SuppressedForCurrentOutage,PowerGuardState.Stopping,PowerGuardState.MonitoringFault,PowerGuardState.Disabled],
        [PowerGuardState.Online]=[PowerGuardState.SuspectedOffline,PowerGuardState.Stopping,PowerGuardState.MonitoringFault,PowerGuardState.Disabled],
        [PowerGuardState.SuspectedOffline]=[PowerGuardState.Online,PowerGuardState.Recovering,PowerGuardState.Countdown,PowerGuardState.Stopping,PowerGuardState.MonitoringFault,PowerGuardState.Disabled],
        [PowerGuardState.Countdown]=[PowerGuardState.Recovering,PowerGuardState.SuppressedForCurrentOutage,PowerGuardState.ExecutingShutdown,PowerGuardState.Stopping,PowerGuardState.MonitoringFault,PowerGuardState.Disabled],
        [PowerGuardState.Recovering]=[PowerGuardState.Online,PowerGuardState.SuspectedOffline,PowerGuardState.Stopping,PowerGuardState.MonitoringFault,PowerGuardState.Disabled],
        [PowerGuardState.SuppressedForCurrentOutage]=[PowerGuardState.Recovering,PowerGuardState.Online,PowerGuardState.SuspectedOffline,PowerGuardState.Stopping,PowerGuardState.MonitoringFault,PowerGuardState.Disabled],
        [PowerGuardState.ExecutingShutdown]=[PowerGuardState.ActionFailed,PowerGuardState.Stopping],
        [PowerGuardState.ActionFailed]=[PowerGuardState.Recovering,PowerGuardState.StartupGrace,PowerGuardState.ExecutingShutdown,PowerGuardState.Stopping,PowerGuardState.Disabled],
        [PowerGuardState.MonitoringFault]=[PowerGuardState.StartupGrace,PowerGuardState.Stopping,PowerGuardState.Disabled],
        [PowerGuardState.Stopping]=[PowerGuardState.Disabled]
    };
    public PowerGuardState State { get; private set; } = PowerGuardState.Disabled;
    public DateTimeOffset StateSinceUtc { get; private set; } = DateTimeOffset.UtcNow;
    public PowerGuardTransition MoveTo(PowerGuardState next, DateTimeOffset now)
    {
        var transition = new PowerGuardTransition(State, next, now);
        State = next;
        StateSinceUtc = now;
        return transition;
    }
    public bool TryMoveTo(PowerGuardState next, DateTimeOffset now, out PowerGuardTransition? transition)
    {
        if (next==State) { transition=new(State,next,now); return true; }
        if (!Allowed.GetValueOrDefault(State,[]).Contains(next)) { transition=null; return false; }
        transition=MoveTo(next,now); return true;
    }
}
