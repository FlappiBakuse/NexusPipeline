namespace NexusPipeline.Core.Execution;

public sealed record ExecutionBudgetSnapshot(long ConsumedMilliseconds, TimeSpan Remaining, string? ExhaustionReason)
{
    public bool IsExhausted => Remaining == TimeSpan.Zero;
}

public sealed class ExecutionTimeBudget
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly long _startedTimestamp;
    private readonly TimeSpan _previouslyConsumed;
    private readonly TimeSpan _total;
    private readonly DateTimeOffset _deadline;
    private TimeSpan _maximumElapsed;
    private TimeSpan? _minimumRemaining;

    public ExecutionTimeBudget(TimeProvider clock, long startedTimestamp, TimeSpan total, DateTimeOffset deadlineUtc,
        TimeSpan previouslyConsumed = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var unlimited = total == TimeSpan.Zero && deadlineUtc == DateTimeOffset.MaxValue;
        if (!unlimited && total <= TimeSpan.Zero || total > TimeSpan.FromDays(7) || total.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            previouslyConsumed < TimeSpan.Zero || !unlimited && previouslyConsumed > total ||
            previouslyConsumed.Ticks % TimeSpan.TicksPerMillisecond != 0 || deadlineUtc == default || deadlineUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("execution_time_budget_invalid");
        _clock = clock; _startedTimestamp = startedTimestamp; _total = total;
        _previouslyConsumed = previouslyConsumed; _deadline = deadlineUtc;
    }

    public ExecutionBudgetSnapshot Read()
    {
        lock (_gate)
        {
            var elapsed = _clock.GetElapsedTime(_startedTimestamp, _clock.GetTimestamp());
            if (elapsed < TimeSpan.Zero || elapsed < _maximumElapsed)
                throw new InvalidOperationException("execution_monotonic_clock_regressed");
            var sinceLastRead = elapsed - _maximumElapsed;
            _maximumElapsed = elapsed;
            if (_total == TimeSpan.Zero)
            {
                var consumedUnlimited = checked(_previouslyConsumed + elapsed);
                return new((consumedUnlimited.Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond,
                    TimeSpan.MaxValue, null);
            }
            var consumed = elapsed >= _total - _previouslyConsumed ? _total : _previouslyConsumed + elapsed;
            var remaining = _total - consumed;
            var deadlineRemaining = _deadline - _clock.GetUtcNow();
            var reason = remaining == TimeSpan.Zero ? "execution_total_budget_exhausted" : null;
            if (deadlineRemaining <= TimeSpan.Zero) { remaining = TimeSpan.Zero; reason ??= "execution_deadline_expired"; }
            else if (deadlineRemaining < remaining) remaining = deadlineRemaining;
            // Once a deadline has reduced this generation's budget, wall-clock rollback cannot restore it.
            if (_minimumRemaining is { } prior)
            {
                var priorRemaining = sinceLastRead >= prior ? TimeSpan.Zero : prior - sinceLastRead;
                if (priorRemaining < remaining) remaining = priorRemaining;
            }
            _minimumRemaining = remaining;
            if (remaining == TimeSpan.Zero) reason ??= "execution_deadline_expired";
            var milliseconds = ((_total - remaining).Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond;
            return new(milliseconds, remaining, reason);
        }
    }

    public TimeSpan Limit(TimeSpan requested)
    {
        if (requested <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requested));
        var remaining = Read().Remaining;
        return requested < remaining ? requested : remaining;
    }
}
