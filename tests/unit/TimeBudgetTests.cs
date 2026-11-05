using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class TimeBudgetTests
{
    [Fact(DisplayName = "Preparation Queueing And Later Steps Share The Original Monotonic Budget Instead Of Starting Another Clock")]
    public void SharesOriginalBudget()
    {
        var clock = new Clock(); var start = clock.GetTimestamp(); var deadline = clock.Now.AddMinutes(1);
        clock.Advance(TimeSpan.FromSeconds(12));
        var budget = new ExecutionTimeBudget(clock, start, TimeSpan.FromMinutes(1), deadline);
        Assert.Equal(TimeSpan.FromSeconds(48), budget.Read().Remaining);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(18), budget.Limit(TimeSpan.FromSeconds(30)));
        clock.Advance(TimeSpan.FromSeconds(18));
        Assert.Equal("execution_total_budget_exhausted", budget.Read().ExhaustionReason);
        Assert.Equal(TimeSpan.Zero, budget.Limit(TimeSpan.FromSeconds(1)));
    }

    [Fact(DisplayName = "Wall Clock Rollback Cannot Add Time To The Monotonic Budget Or Reopen An Observed Deadline")]
    public void RejectsClockExtensions()
    {
        var clock = new Clock(); var budget = new ExecutionTimeBudget(clock, clock.GetTimestamp(), TimeSpan.FromMinutes(1), clock.Now.AddMinutes(1));
        clock.Advance(TimeSpan.FromSeconds(20)); clock.Now = clock.Now.AddDays(-1);
        Assert.Equal(TimeSpan.FromSeconds(40), budget.Read().Remaining);
        clock.Now = clock.Now.AddDays(2);
        Assert.Equal("execution_deadline_expired", budget.Read().ExhaustionReason);
        clock.Now = clock.Now.AddDays(-2);
        Assert.True(budget.Read().IsExhausted);
    }

    [Fact(DisplayName = "A Deadline Reduction Keeps Consuming Monotonic Time After The Wall Clock Moves Back")]
    public void KeepsReducedDeadline()
    {
        var clock = new Clock(); var deadline = clock.Now.AddMinutes(1);
        var budget = new ExecutionTimeBudget(clock, clock.GetTimestamp(), TimeSpan.FromMinutes(1), deadline);
        clock.Now = deadline.AddSeconds(-10);
        Assert.Equal(TimeSpan.FromSeconds(10), budget.Read().Remaining);
        clock.Now = clock.Now.AddDays(-1); clock.Advance(TimeSpan.FromSeconds(7));
        var saved = budget.Read();
        Assert.Equal(TimeSpan.FromSeconds(3), saved.Remaining); Assert.Equal(57000, saved.ConsumedMilliseconds);
        var restored = new ExecutionTimeBudget(clock, clock.GetTimestamp(), TimeSpan.FromMinutes(1), deadline,
            TimeSpan.FromMilliseconds(saved.ConsumedMilliseconds));
        Assert.Equal(TimeSpan.FromSeconds(3), restored.Read().Remaining);
    }

    [Fact(DisplayName = "A Rehydrated Budget Keeps Consumed Time And Rounds The Checkpoint Up Without Gaining Fractional Milliseconds")]
    public void PreservesConsumedTime()
    {
        var clock = new Clock(); var budget = new ExecutionTimeBudget(clock, clock.GetTimestamp(), TimeSpan.FromSeconds(1), clock.Now.AddSeconds(1));
        clock.Advance(TimeSpan.FromTicks(10001));
        var saved = budget.Read(); Assert.Equal(2, saved.ConsumedMilliseconds);
        var restored = new ExecutionTimeBudget(clock, clock.GetTimestamp(), TimeSpan.FromSeconds(1), clock.Now.AddSeconds(1),
            TimeSpan.FromMilliseconds(saved.ConsumedMilliseconds));
        Assert.Equal(TimeSpan.FromMilliseconds(998), restored.Read().Remaining);
        clock.Advance(TimeSpan.FromMilliseconds(998));
        Assert.True(restored.Read().IsExhausted); Assert.Equal(1000, restored.Read().ConsumedMilliseconds);
    }

    [Fact(DisplayName = "A Regressing Monotonic Source Is Rejected Instead Of Returning More Execution Time")]
    public void RejectsRegressingClocks()
    {
        var clock = new Clock(); var budget = new ExecutionTimeBudget(clock, clock.GetTimestamp(), TimeSpan.FromMinutes(1), clock.Now.AddMinutes(1));
        clock.Advance(TimeSpan.FromSeconds(5)); _ = budget.Read();
        clock.Timestamp--;
        Assert.Equal("execution_monotonic_clock_regressed", Assert.Throws<InvalidOperationException>(() => budget.Read()).Message);
    }

    [Theory(DisplayName = "An Invalid Persisted Budget Cannot Become A New Execution Window")]
    [InlineData(0, 0)]
    [InlineData(604801000, 0)]
    [InlineData(1000, -1)]
    [InlineData(1000, 1001)]
    public void RejectsInvalidBudgets(long total, long consumed)
    {
        var clock = new Clock();
        Assert.Throws<ArgumentException>(() => new ExecutionTimeBudget(clock, 0, TimeSpan.FromMilliseconds(total), clock.Now.AddMinutes(1),
            TimeSpan.FromMilliseconds(consumed)));
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-21T00:00:00Z");
        internal long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Now;
        internal void Advance(TimeSpan amount) { Timestamp += amount.Ticks; Now += amount; }
    }
}
