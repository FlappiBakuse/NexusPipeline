using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Settings;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class RetryHookTests
{
    private static readonly Guid Run = Guid.Parse("95de4b59-0a27-4e71-9586-8b3747e34a81");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
    private static ExecutionRetrySurface Worker => new(new(RetrySafety.Safe, "workerAndUpstream", "stable-run-binding/1",
        [AttemptOutcome.Failed, AttemptOutcome.Interrupted]), 3, true, false, false, true, false)
    { Hooks = [new(ExecutionHookRole.Pre, true, false), new(ExecutionHookRole.Post, false, true)] };
    private static FrozenRetryPolicy Policy => RetryAuthorityPolicy.Capture(Run, [Worker], new(true, 0), Now.AddMinutes(5));

    [Fact(DisplayName = "A Single Declared Binding Has An Explicit Once Pre Final Post Authority Scope")]
    public void BindsHookScope()
    {
        var description = RetryHookPolicy.Describe([Worker]);
        Assert.Equal(RetrySafety.Safe, description.Safety); Assert.Equal("once-pre-final-post", description.Scope);
        Assert.Equal("retry_authority_verified", description.ReasonCode); Assert.Equal(3, Policy.MaximumAttempts);
    }

    [Theory(DisplayName = "Undeclared Repeatable Hook Effects Never Borrow Worker Safety")]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RequiresHookSafety(bool once, bool final)
    {
        var worker = Worker with { Hooks = [new(ExecutionHookRole.Pre, once, false), new(ExecutionHookRole.Post, false, final)] };
        var result = RetryHookPolicy.Describe([worker]);
        Assert.Equal(RetrySafety.Unknown, result.Safety); Assert.Equal("retry_hook_effects_unverified", result.ReasonCode);
    }

    [Fact(DisplayName = "Multi Binding Final Post Does Not Guess The Future Whole Attempt Decision")]
    public void RejectsAmbiguousDeferral()
    {
        var result = RetryHookPolicy.Describe([Worker, Worker]);
        Assert.Equal(RetrySafety.Unknown, result.Safety); Assert.Equal("retry_hook_finality_unverified", result.ReasonCode);
        Assert.Equal(1, RetryAuthorityPolicy.Capture(Run, [Worker, Worker], new(true, 0), Now.AddMinutes(5)).MaximumAttempts);
    }

    [Fact(DisplayName = "Post Deferral Is Conditional And Does Not Claim Configuration Has Already Been Restored")]
    public void PreservesConditionalDeferral()
    {
        var actual = new RetryEvidence(AttemptOutcome.Failed, WriterState.Stopped, false, false, true, 1, Now);
        var budget = new ExecutionBudgetSnapshot(5000, TimeSpan.FromMinutes(1), null);
        Assert.Equal("recovery_not_confirmed", RetryDecision.Evaluate(Policy, actual, budget, Now).ReasonCode);
        var decision = RetryDecision.EvaluatePostDeferral(Policy, actual, budget, Now);
        Assert.True(decision.ShouldDefer); Assert.True(decision.RequiresConfigurationRestoration);
        Assert.Equal("retry_post_pending_restoration", decision.ReasonCode);
        Assert.True(RetryDecision.Evaluate(Policy, actual with { IsConfigurationRestored = true }, budget, Now).IsEligible);
    }

    [Theory(DisplayName = "A Post Cannot Be Deferred When Another Actual Gate Has Already Rejected Retry")]
    [InlineData("cancelled")]
    [InlineData("partial")]
    [InlineData("writers")]
    [InlineData("budget")]
    [InlineData("approval")]
    [InlineData("attempt-limit")]
    public void HonorsRejectedRetry(string cause)
    {
        var policy = Policy;
        var evidence = new RetryEvidence(AttemptOutcome.Failed, WriterState.Stopped, false, false, true, 1, Now);
        var budget = new ExecutionBudgetSnapshot(5000, TimeSpan.FromMinutes(1), null);
        switch (cause)
        {
            case "cancelled": evidence = evidence with { PreviousOutcome = AttemptOutcome.Cancelled }; break;
            case "partial": evidence = evidence with { PreviousOutcome = AttemptOutcome.Partial }; break;
            case "writers": evidence = evidence with { Writers = WriterState.Unknown }; break;
            case "budget": budget = budget with { Remaining = TimeSpan.Zero }; break;
            case "approval": policy = policy with { HostApproved = false }; break;
            default: evidence = evidence with { CompletedAttempts = 3 }; break;
        }
        Assert.False(RetryDecision.EvaluatePostDeferral(policy, evidence, budget, Now).ShouldDefer);
    }

    [Theory(DisplayName = "Pre Safety Distinguishes Unstarted Jobs From Failed Or Unknown Effects")]
    [InlineData(EngineOutcome.Failed, false)]
    [InlineData(EngineOutcome.Unknown, false)]
    [InlineData(EngineOutcome.NotStarted, true)]
    [InlineData(EngineOutcome.Succeeded, true)]
    public void DistinguishesUnstartedHooks(EngineOutcome pre, bool expected)
    {
        Assert.Equal(expected, RetryHookPolicy.CanRetry(Worker.Hooks,
            [new(ExecutionHookRole.Pre, RetryHookDisposition.Collected, pre),
                new(ExecutionHookRole.Post, RetryHookDisposition.Deferred, EngineOutcome.NotStarted)]));
    }

    [Theory(DisplayName = "An Executed Post Never Authorizes Repeating The Worker")]
    [InlineData(EngineOutcome.Failed)]
    [InlineData(EngineOutcome.Succeeded)]
    [InlineData(EngineOutcome.Unknown)]
    public void RejectsExecutedPost(EngineOutcome post)
    {
        Assert.False(RetryHookPolicy.CanRetry(Worker.Hooks,
            [new(ExecutionHookRole.Pre, RetryHookDisposition.InheritedSuccess, EngineOutcome.Succeeded),
                new(ExecutionHookRole.Post, RetryHookDisposition.Collected, post)]));
    }

    [Fact(DisplayName = "Collected Primary Classification Preserves Partial And Cancelled Before Failed")]
    public void KeepsPrimaryClassification()
    {
        Assert.Equal(AttemptOutcome.Partial, RetryDecision.ClassifyCollectedPrimary(new("binding", EngineOutcome.Failed,
            BusinessOutcome.Partial, "fixture")));
        Assert.Equal(AttemptOutcome.Cancelled, RetryDecision.ClassifyCollectedPrimary(new("binding", EngineOutcome.Failed,
            BusinessOutcome.Cancelled, "fixture")));
        Assert.Equal(AttemptOutcome.Unverified, RetryDecision.ClassifyCollectedPrimary(new("binding", EngineOutcome.NotStarted,
            BusinessOutcome.Unverified, "fixture")));
        Assert.Throws<ArgumentException>(() => RetryDecision.ClassifyCollectedPrimary(new("binding", EngineOutcome.Running,
            BusinessOutcome.Unverified, "fixture")));
    }
}
