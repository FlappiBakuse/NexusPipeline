namespace NexusPipeline.StorageTests;

internal sealed class CompletionControlledClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly DateTimeOffset _origin = DateTimeOffset.UtcNow;
    private readonly HashSet<ControlledTimer> _timers = [];
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _origin.AddTicks(_ticks); }
    internal bool HasCountdownTimer { get { lock (_gate) return _timers.Any(timer => timer.Due - _ticks >= TimeSpan.FromSeconds(50).Ticks); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ControlledTimer(this, callback, state);
        timer.Change(dueTime, period); return timer;
    }

    internal void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        List<ControlledTimer> ready;
        lock (_gate)
        {
            _ticks = checked(_ticks + elapsed.Ticks);
            ready = _timers.Where(timer => timer.Due <= _ticks).ToList();
            foreach (var timer in ready)
            {
                if (timer.Period == Timeout.InfiniteTimeSpan) { _timers.Remove(timer); timer.Due = long.MaxValue; }
                else timer.Due = checked(_ticks + timer.Period.Ticks);
            }
        }
        foreach (var timer in ready) timer.Fire();
    }

    private sealed class ControlledTimer(CompletionControlledClock owner, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        internal long Due { get; set; } = long.MaxValue;
        internal TimeSpan Period { get; private set; } = Timeout.InfiniteTimeSpan;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan ||
                period <= TimeSpan.Zero && period != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(dueTime));
            lock (owner._gate)
            {
                if (_disposed) return false;
                Period = period; Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(owner._ticks + dueTime.Ticks);
                if (Due == long.MaxValue) owner._timers.Remove(this); else owner._timers.Add(this);
                return true;
            }
        }
        internal void Fire() { lock (owner._gate) if (_disposed) return; callback(state); }
        public void Dispose() { lock (owner._gate) { _disposed = true; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
