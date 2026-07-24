namespace QingToolbox.Shell.WebShell;

public sealed class WebProcessFailureGate
{
    private readonly object _sync = new();
    private readonly HashSet<long> _acceptedGenerations = [];
    private object? _core;
    private long _generation;
    private CancellationToken _session;

    public void Bind(object core, long generation, CancellationToken session)
    {
        lock (_sync)
        {
            _core = core;
            _generation = generation;
            _session = session;
        }
    }

    public bool TryAccept(object core, long generation, CancellationToken session, bool hostDisposed)
    {
        lock (_sync)
        {
            return !hostDisposed
                && !session.IsCancellationRequested
                && !_session.IsCancellationRequested
                && ReferenceEquals(core, _core)
                && generation == _generation
                && session == _session
                && _acceptedGenerations.Add(generation);
        }
    }

    public void Unbind()
    {
        lock (_sync)
        {
            _core = null;
            _generation = 0;
            _session = default;
        }
    }
}
