namespace FluffHistory.Tests.Infrastructure;

/// <summary>Deterministic monotonic timers with independently movable UTC. Advance delivers each due timer
/// at the new instant (not every intermediate tick), modelling a suspended/delayed process.</summary>
internal sealed class ManualSchedulerTime(DateTimeOffset initial) : TimeProvider
{
    private readonly object _gate = new();
    private readonly HashSet<ManualTimer> _timers = [];
    private DateTimeOffset _utc = initial;
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _utc; }
    public override long GetTimestamp() { lock (_gate) return _timestamp; }
    internal int TimerCount { get { lock (_gate) return _timers.Count; } }
    internal void SetUtcNow(DateTimeOffset value) { lock (_gate) _utc = value; }
    internal void Advance(TimeSpan elapsed)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _utc += elapsed; _timestamp += elapsed.Ticks;
            due = _timers.Where(t => t.Due is { } deadline && deadline <= _timestamp).ToArray();
            foreach (var timer in due) timer.Due = null;
        }
        foreach (var timer in due) timer.Fire();
    }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period); return timer;
    }
    private sealed class ManualTimer(ManualSchedulerTime owner, TimerCallback callback, object? state) : ITimer
    {
        internal long? Due;
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Tests expect one-shot timers.");
            lock (owner._gate)
            {
                if (_disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._timestamp + dueTime.Ticks;
                return true;
            }
        }
        internal void Fire() { lock (owner._gate) { if (_disposed) return; } callback(state); }
        public void Dispose() { lock (owner._gate) { _disposed = true; Due = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
