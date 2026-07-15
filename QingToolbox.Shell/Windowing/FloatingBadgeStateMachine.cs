namespace QingToolbox.Shell.Windowing;

public enum FloatingBadgeState { Normal, EnteringBadge, Badge, Restoring, Exiting }

public sealed class FloatingBadgeStateMachine
{
    public FloatingBadgeState State { get; private set; } = FloatingBadgeState.Normal;
    public bool TryBeginEnter() => TryTransition(FloatingBadgeState.Normal, FloatingBadgeState.EnteringBadge);
    public bool TryCompleteEnter() => TryTransition(FloatingBadgeState.EnteringBadge, FloatingBadgeState.Badge);
    public bool TryFailEnter() => TryTransition(FloatingBadgeState.EnteringBadge, FloatingBadgeState.Normal);
    public bool TryBeginRestore() => TryTransition(FloatingBadgeState.Badge, FloatingBadgeState.Restoring);
    public bool TryCompleteRestore() => TryTransition(FloatingBadgeState.Restoring, FloatingBadgeState.Normal);
    public bool TryFailRestore() => TryTransition(FloatingBadgeState.Restoring, FloatingBadgeState.Badge);
    public bool TryBeginExit()
    {
        if (State == FloatingBadgeState.Exiting) return false;
        State = FloatingBadgeState.Exiting;
        return true;
    }
    private bool TryTransition(FloatingBadgeState expected, FloatingBadgeState next)
    {
        if (State != expected) return false;
        State = next;
        return true;
    }
}
