using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class AttemptFactsTests
{
    [Theory(DisplayName = "Multi Step Business Aggregation Preserves Partial And Unknown Coverage Without Promoting One Successful Step")]
    [InlineData(BusinessOutcome.Succeeded, BusinessOutcome.Unverified, BusinessOutcome.Unverified)]
    [InlineData(BusinessOutcome.Succeeded, BusinessOutcome.Failed, BusinessOutcome.Partial)]
    [InlineData(BusinessOutcome.Succeeded, BusinessOutcome.Partial, BusinessOutcome.Partial)]
    [InlineData(BusinessOutcome.Failed, BusinessOutcome.Failed, BusinessOutcome.Failed)]
    [InlineData(BusinessOutcome.Succeeded, BusinessOutcome.Succeeded, BusinessOutcome.Succeeded)]
    [InlineData(BusinessOutcome.NotApplicable, BusinessOutcome.Succeeded, BusinessOutcome.Succeeded)]
    [InlineData(BusinessOutcome.NotApplicable, BusinessOutcome.NotApplicable, BusinessOutcome.NotApplicable)]
    [InlineData(BusinessOutcome.Cancelled, BusinessOutcome.Succeeded, BusinessOutcome.Unverified)]
    [InlineData(BusinessOutcome.Cancelled, BusinessOutcome.Cancelled, BusinessOutcome.Cancelled)]
    [InlineData(BusinessOutcome.Failed, BusinessOutcome.Unverified, BusinessOutcome.Unverified)]
    public void AggregatesPartialBusiness(
        BusinessOutcome first, BusinessOutcome second, BusinessOutcome expected)
    {
        var steps = new[] { Step("first", EngineOutcome.Succeeded, first), Step("second", EngineOutcome.Succeeded, second) };
        var result = Aggregate(steps);
        Assert.Equal(expected, result.Business); Assert.Equal(EngineOutcome.Succeeded, result.Engine);
        Assert.Equal(first, steps[0].Business); Assert.Equal(second, steps[1].Business);
    }

    [Theory(DisplayName = "Engine Aggregation Remains Independent Of Business And Keeps Unknown Or Failed Execution Evidence")]
    [InlineData(EngineOutcome.Succeeded, EngineOutcome.Failed, EngineOutcome.Failed)]
    [InlineData(EngineOutcome.Cancelled, EngineOutcome.Succeeded, EngineOutcome.Cancelled)]
    [InlineData(EngineOutcome.Unknown, EngineOutcome.Failed, EngineOutcome.Unknown)]
    [InlineData(EngineOutcome.NotStarted, EngineOutcome.Succeeded, EngineOutcome.Succeeded)]
    [InlineData(EngineOutcome.NotStarted, EngineOutcome.NotStarted, EngineOutcome.NotStarted)]
    [InlineData(EngineOutcome.Failed, EngineOutcome.Cancelled, EngineOutcome.Failed)]
    public void SeparatesEngineFacts(
        EngineOutcome first, EngineOutcome second, EngineOutcome expected)
    {
        var result = Aggregate([Step("first", first, BusinessOutcome.Unverified), Step("second", second, BusinessOutcome.Unverified)],
            expected == EngineOutcome.NotStarted ? LifecycleOutcome.Cancelled : LifecycleOutcome.Completed);
        Assert.Equal(expected, result.Engine); Assert.Equal(BusinessOutcome.Unverified, result.Business);
    }

    [Fact(DisplayName = "Cancellation Of An Unstarted Successor And Restoration Failure Do Not Erase The Earlier Steps Immutable Facts")]
    public void PreservesEarlierFacts()
    {
        var first = Step("first", EngineOutcome.Succeeded, BusinessOutcome.Succeeded);
        var cancelled = Aggregate([first, Step("second", EngineOutcome.NotStarted, BusinessOutcome.Unverified)], LifecycleOutcome.Cancelled);
        Assert.Equal(EngineOutcome.Succeeded, cancelled.Engine); Assert.Equal(BusinessOutcome.Unverified, cancelled.Business);
        Assert.Equal(LifecycleOutcome.Cancelled, cancelled.Lifecycle);
        var quarantined = AttemptFactsPolicy.Aggregate(Guid.NewGuid(), Guid.NewGuid(), 1, [first],
            LifecycleOutcome.Interrupted, RecoveryOutcome.Quarantined, "configuration_not_restored");
        Assert.Equal(BusinessOutcome.Succeeded, quarantined.Business); Assert.Equal(EngineOutcome.Succeeded, quarantined.Engine);
        Assert.Equal(LifecycleOutcome.Interrupted, quarantined.Lifecycle); Assert.Equal(RecoveryOutcome.Quarantined, quarantined.Recovery);
        Assert.Equal(BusinessOutcome.Succeeded, first.Business);
    }

    [Theory(DisplayName = "Contradictory Step Or Lifecycle Facts Cannot Become A Terminal Attempt")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("running")]
    [InlineData("unstartedBusiness")]
    [InlineData("unstartedCompleted")]
    [InlineData("startedSkipped")]
    [InlineData("pending")]
    public void RejectsContradictoryFacts(string invalid)
    {
        var first = Step("first", EngineOutcome.Succeeded, BusinessOutcome.Unverified);
        IReadOnlyList<ExecutionStepFact> steps = invalid switch
        {
            "duplicate" => [first, first], "empty" => [], "running" => [first with { Engine = EngineOutcome.Running }],
            "unstartedBusiness" => [first with { Engine = EngineOutcome.NotStarted, Business = BusinessOutcome.Succeeded }],
            "unstartedCompleted" => [first with { Engine = EngineOutcome.NotStarted }], _ => [first]
        };
        Assert.Throws<ArgumentException>(() => AttemptFactsPolicy.Aggregate(Guid.NewGuid(), Guid.NewGuid(), 1, steps,
            invalid == "startedSkipped" ? LifecycleOutcome.Skipped : LifecycleOutcome.Completed,
            invalid == "pending" ? RecoveryOutcome.Pending : RecoveryOutcome.Restored, "execution_completed"));
    }

    private static ExecutionStepFact Step(string id, EngineOutcome engine, BusinessOutcome business) => new(id, engine, business, "step_collected");
    private static AttemptFacts Aggregate(IReadOnlyList<ExecutionStepFact> steps, LifecycleOutcome lifecycle = LifecycleOutcome.Completed) =>
        AttemptFactsPolicy.Aggregate(Guid.NewGuid(), Guid.NewGuid(), 1, steps, lifecycle, RecoveryOutcome.Restored, "execution_completed");
}
