namespace QingToolbox.Shell.Windowing;

public enum FloatingBadgeState { Normal, EnteringBadge, Badge, Restoring, Exiting }

public sealed class FloatingBadgeStateMachine
{
    public FloatingBadgeState State { get; private set; } = FloatingBadgeState.Normal;
    public bool TryBeginEnter() => TryTransition(FloatingBadgeState.Normal, FloatingBadgeState.EnteringBadge);
    public void CompleteEnter() => RequireTransition(FloatingBadgeState.EnteringBadge, FloatingBadgeState.Badge);
    public void FailEnter() => RequireTransition(FloatingBadgeState.EnteringBadge, FloatingBadgeState.Normal);
    public bool TryBeginRestore() => TryTransition(FloatingBadgeState.Badge, FloatingBadgeState.Restoring);
    public void CompleteRestore() => RequireTransition(FloatingBadgeState.Restoring, FloatingBadgeState.Normal);
    public void FailRestore() => RequireTransition(FloatingBadgeState.Restoring, FloatingBadgeState.Badge);
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
    private void RequireTransition(FloatingBadgeState expected, FloatingBadgeState next)
    {
        if (State != expected) throw new InvalidOperationException($"Expected {expected}, but was {State}.");
        State = next;
    }
}
