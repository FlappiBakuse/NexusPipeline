using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

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

    [Fact(DisplayName = "Persistent Budget And Deadline Cannot Be Reset By Restart Or Repeated Evaluation")]
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
        Assert.Equal(2, evidence.CompletedAttempts);
        Assert.Equal("business-1", policy.IdempotencyKey);
    }
}
