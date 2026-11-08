using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

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
        Assert.Equal(engine, facts.Engine); Assert.Equal(business, facts.Business); Assert.Equal(lifecycle, facts.Lifecycle);
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
