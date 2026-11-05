using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class RetryBudgetTests
{
    [Theory(DisplayName = "Backoff Must Leave Actual Shared Budget For Another Attempt")]
    [InlineData(29, false, "retry_total_budget_exhausted")]
    [InlineData(30, false, "retry_total_budget_exhausted")]
    [InlineData(31, false, "retry_backoff")]
    public void RequiresRemainingBudget(int remainingSeconds, bool eligible, string reason)
    {
        var now = new DateTimeOffset(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);
        var decision = RetryDecision.Evaluate(new(RetrySafety.Safe, true, 3, now.AddMinutes(5), TimeSpan.FromSeconds(30), "actual-business-key"),
            new(AttemptOutcome.Interrupted, WriterState.Stopped, true, false, true, 1, now),
            new(120000 - remainingSeconds * 1000, TimeSpan.FromSeconds(remainingSeconds), null), now);
        Assert.Equal(eligible, decision.IsEligible); Assert.Equal(reason, decision.ReasonCode);
    }

    [Theory(DisplayName = "Unknown Enums And Impossible Future Receipt Times Never Authorize Retry")]
    [InlineData("outcome")]
    [InlineData("writer")]
    [InlineData("safety")]
    [InlineData("future-end")]
    public void RejectsInvalidReceipts(string damage)
    {
        var now = new DateTimeOffset(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);
        var policy = new FrozenRetryPolicy(RetrySafety.Safe, true, 3, now.AddMinutes(5), TimeSpan.Zero, "actual-business-key");
        var evidence = new RetryEvidence(AttemptOutcome.Interrupted, WriterState.Stopped, true, false, true, 1, now);
        if (damage == "outcome") evidence = evidence with { PreviousOutcome = (AttemptOutcome)99 };
        if (damage == "writer") evidence = evidence with { Writers = (WriterState)99 };
        if (damage == "safety") policy = policy with { Safety = (RetrySafety)99 };
        if (damage == "future-end") evidence = evidence with { PreviousEndedAt = now.AddSeconds(1) };
        Assert.Equal("retry_evidence_invalid", RetryDecision.Evaluate(policy, evidence, new(1000, TimeSpan.FromSeconds(119), null), now).ReasonCode);
    }

    [Fact(DisplayName = "Shared Budget And Original Deadline Remain The Same After Wall Clock Rollback")]
    public void KeepsOriginalDeadline()
    {
        var clock = new ManualClock();
        var deadline = clock.GetUtcNow().AddMinutes(2);
        var budget = new ExecutionTimeBudget(clock, clock.GetTimestamp(), TimeSpan.FromMinutes(2), deadline, TimeSpan.FromSeconds(80));
        clock.Advance(TimeSpan.FromSeconds(30));
        var first = budget.Read();
        clock.Wall = clock.Wall.AddHours(-1); clock.Advance(TimeSpan.FromSeconds(9));
        var second = budget.Read();
        Assert.True(second.Remaining < first.Remaining);
        Assert.True(second.ConsumedMilliseconds > first.ConsumedMilliseconds);
        clock.Advance(TimeSpan.FromSeconds(1)); Assert.True(budget.Read().IsExhausted);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        internal DateTimeOffset Wall { get; set; } = new(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => Wall;
        internal void Advance(TimeSpan elapsed) { _ticks += elapsed.Ticks; Wall += elapsed; }
    }
}
