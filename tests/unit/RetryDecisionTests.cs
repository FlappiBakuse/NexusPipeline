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

public sealed class RetryRulesTests
{
    [Fact(DisplayName = "Unknown Or Unapproved Tasks Never Retry Even After Configuration Restores")]
    public void RequiresApprovedSafety()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new FrozenRetryPolicy(RetrySafety.Safe, true, 3, now.AddMinutes(5), TimeSpan.Zero, "business-1");
        var evidence = new RetryEvidence(AttemptOutcome.Interrupted, WriterState.Stopped, true, false, true, 1, now);
        Assert.True(RetryDecision.Evaluate(policy, evidence, now).IsEligible);
        Assert.False(RetryDecision.Evaluate(policy with { Safety = RetrySafety.Unknown }, evidence, now).IsEligible);
        Assert.False(RetryDecision.Evaluate(policy with { HostApproved = false }, evidence, now).IsEligible);
        Assert.False(RetryDecision.Evaluate(policy, evidence with { Writers = WriterState.Unknown }, now).IsEligible);
        Assert.False(RetryDecision.Evaluate(policy, evidence with { IsConfigurationRestored = false }, now).IsEligible);
        Assert.False(RetryDecision.Evaluate(policy, evidence with { HasQuarantinedResources = true }, now).IsEligible);
        Assert.False(RetryDecision.Evaluate(policy, evidence with { AreFrozenIdentitiesValid = false }, now).IsEligible);
    }

    [Fact(DisplayName = "Retry Eligibility Uses The Recorded Attempt Count Original Deadline And Backoff")]
    public void KeepsDurableBudget()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new FrozenRetryPolicy(RetrySafety.Safe, true, 3, now.AddMinutes(5), TimeSpan.FromSeconds(30), "business-1");
        var evidence = new RetryEvidence(AttemptOutcome.Interrupted, WriterState.Stopped, true, false, true, 2, now);
        Assert.Equal("retry_backoff", RetryDecision.Evaluate(policy, evidence, now).ReasonCode);
        Assert.Equal(now.AddSeconds(30), RetryDecision.Evaluate(policy, evidence, now).NotBefore);
        Assert.True(RetryDecision.Evaluate(policy, evidence, now.AddSeconds(30)).IsEligible);
        Assert.Equal("attempt_budget_exhausted", RetryDecision.Evaluate(policy, evidence with { CompletedAttempts = 3 }, now).ReasonCode);
        Assert.False(RetryDecision.Evaluate(policy, evidence, now.AddMinutes(5)).IsEligible);
        Assert.False(RetryDecision.Evaluate(policy, evidence with { PreviousOutcome = AttemptOutcome.Succeeded }, now.AddSeconds(30)).IsEligible);
    }
}

public sealed class RetryClassificationTests
{
    [Theory(DisplayName = "Collected Dependency Facts Respect Business Success And Reject Unproven Completion")]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.Succeeded, "execution_business_succeeded", AttemptOutcome.Succeeded)]
    [InlineData(EngineOutcome.Unknown, BusinessOutcome.Succeeded, "execution_success_grace_expired", AttemptOutcome.Succeeded)]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.NotApplicable, "execution_judgement_not_applicable", AttemptOutcome.Succeeded)]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Succeeded, "fixture", AttemptOutcome.Succeeded)]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.Partial, "execution_business_succeeded", AttemptOutcome.Partial)]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.Cancelled, "execution_business_succeeded", AttemptOutcome.Cancelled)]
    [InlineData(EngineOutcome.Cancelled, BusinessOutcome.Succeeded, "execution_business_succeeded", AttemptOutcome.Cancelled)]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.Failed, "execution_business_failed", AttemptOutcome.Failed)]
    [InlineData(EngineOutcome.Unknown, BusinessOutcome.Unverified, "fixture", AttemptOutcome.Unverified)]
    [InlineData(EngineOutcome.NotStarted, BusinessOutcome.Unverified, "fixture", AttemptOutcome.Unverified)]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Failed, "execution_completion_missing", AttemptOutcome.Failed)]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Unverified, "execution_engine_succeeded", AttemptOutcome.Unverified)]
    [InlineData(EngineOutcome.NotStarted, BusinessOutcome.Succeeded, "execution_business_succeeded", AttemptOutcome.Unverified)]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.NotApplicable, "fixture", AttemptOutcome.Failed)]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.Succeeded, "fixture", AttemptOutcome.Failed)]
    public void ClassifiesCollectedFacts(EngineOutcome engine, BusinessOutcome business, string reason, AttemptOutcome expected)
    {
        var actual = new ExecutionStepFact("predecessor", engine, business, reason);
        Assert.Equal(expected, RetryDecision.ClassifyCollectedPrimary(actual));
    }

    [Fact(DisplayName = "A Running Step Is Rejected By Collected Primary Classification")]
    public void RejectsRunningCollectedFact()
    {
        var fact = new ExecutionStepFact("predecessor", EngineOutcome.Running, BusinessOutcome.Unverified, "fixture");
        Assert.Throws<ArgumentException>(() => RetryDecision.ClassifyCollectedPrimary(fact));
    }

    [Theory(DisplayName = "Original Four Facts Have One Explicit Retry Classification")]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.Partial, LifecycleOutcome.Completed, AttemptOutcome.Partial)]
    [InlineData(EngineOutcome.Cancelled, BusinessOutcome.Failed, LifecycleOutcome.Completed, AttemptOutcome.Cancelled)]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.Cancelled, LifecycleOutcome.Completed, AttemptOutcome.Cancelled)]
    [InlineData(EngineOutcome.Unknown, BusinessOutcome.Failed, LifecycleOutcome.Completed, AttemptOutcome.Failed)]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.Unverified, LifecycleOutcome.Completed, AttemptOutcome.Failed)]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Failed, LifecycleOutcome.Completed, AttemptOutcome.Failed)]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Succeeded, LifecycleOutcome.Completed, AttemptOutcome.Succeeded)]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.NotApplicable, LifecycleOutcome.Completed, AttemptOutcome.Succeeded)]
    [InlineData(EngineOutcome.Unknown, BusinessOutcome.Succeeded, LifecycleOutcome.Completed, AttemptOutcome.Unverified)]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Unverified, LifecycleOutcome.Completed, AttemptOutcome.Unverified)]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Partial, LifecycleOutcome.Interrupted, AttemptOutcome.Interrupted)]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.Failed, LifecycleOutcome.Cancelled, AttemptOutcome.Cancelled)]
    [InlineData(EngineOutcome.NotStarted, BusinessOutcome.Unverified, LifecycleOutcome.Blocked, AttemptOutcome.Blocked)]
    [InlineData(EngineOutcome.NotStarted, BusinessOutcome.Unverified, LifecycleOutcome.Skipped, AttemptOutcome.Skipped)]
    public void ClassifiesOriginalFacts(EngineOutcome engine, BusinessOutcome business,
        LifecycleOutcome lifecycle, AttemptOutcome expected)
    {
        var facts = new AttemptFacts(Guid.NewGuid(), Guid.NewGuid(), 1, engine, business, lifecycle, RecoveryOutcome.Restored, "fixture");
        Assert.Equal(expected, RetryDecision.Classify(facts));
    }

    [Theory(DisplayName = "Unfinished Inputs Never Enter Retry Classification")]
    [InlineData(EngineOutcome.Running, LifecycleOutcome.Completed)]
    [InlineData(EngineOutcome.NotStarted, LifecycleOutcome.Completed)]
    [InlineData(EngineOutcome.NotStarted, LifecycleOutcome.NotStarted)]
    [InlineData(EngineOutcome.Succeeded, LifecycleOutcome.Running)]
    public void RejectsUnfinishedInputs(EngineOutcome engine, LifecycleOutcome lifecycle)
    {
        var facts = new AttemptFacts(Guid.NewGuid(), Guid.NewGuid(), 1, engine, BusinessOutcome.Unverified, lifecycle, RecoveryOutcome.Pending, "fixture");
        Assert.Throws<ArgumentException>(() => RetryDecision.Classify(facts));
    }
}
