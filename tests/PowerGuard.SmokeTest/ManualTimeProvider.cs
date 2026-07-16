using System.Collections.Generic;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow;
    private long _timestamp;
    private long _sequence;

    public ManualTimeProvider(DateTimeOffset? utcNow = null) => _utcNow = utcNow ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() { lock (_sync) return _utcNow; }
    public override long GetTimestamp() { lock (_sync) return _timestamp; }
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public int PendingTimerCount { get { lock (_sync) return _timers.Count(x => !x.IsDisposed); } }
    public void AdjustUtc(TimeSpan delta) { lock (_sync) _utcNow += delta; }

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));
        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_sync)
        {
            _utcNow += delta;
            _timestamp = checked(_timestamp + delta.Ticks);
            while (true)
            {
                var timer = _timers.Where(x => x.IsDue(_timestamp)).OrderBy(x => x.DueTimestamp).ThenBy(x => x.Sequence).FirstOrDefault();
                if (timer is null) break;
                callbacks.Add(timer.Fire(_timestamp));
            }
        }
        foreach (var callback in callbacks) callback.Callback(callback.State);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period, ++_sequence, _timestamp);
            _timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period, long sequence, long now) : ITimer
    {
        private bool _disposed;
        private long _dueTimestamp = Due(now, dueTime);
        private TimeSpan _period = period;
        public long Sequence { get; } = sequence;
        public long DueTimestamp => _dueTimestamp;
        public bool IsDisposed => _disposed;
        public bool IsDue(long timestamp) => !_disposed && _dueTimestamp != long.MaxValue && _dueTimestamp <= timestamp;
        public (TimerCallback Callback, object? State) Fire(long timestamp)
        {
            _dueTimestamp = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan
                ? checked(_dueTimestamp + Math.Max(1, _period.Ticks))
                : long.MaxValue;
            return (callback, state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            lock (owner._sync)
            {
                if (_disposed) return false;
                _period = newPeriod;
                _dueTimestamp = Due(owner._timestamp, dueTime);
                return true;
            }
        }
        public void Dispose() { lock (owner._sync) { _disposed = true; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        private static long Due(long timestamp, TimeSpan dueTime) => dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(timestamp + Math.Max(0, dueTime.Ticks));
    }
}
