using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class PcFactsTests
{
    [Theory(DisplayName = "Target Engine Facts Never Replace The Unstarted Primary Or Invent Business")]
    [InlineData(TargetExecutionOutcome.Skipped, EngineOutcome.NotStarted)]
    [InlineData(TargetExecutionOutcome.ExternalReady, EngineOutcome.NotStarted)]
    [InlineData(TargetExecutionOutcome.OwnedReady, EngineOutcome.Succeeded)]
    [InlineData(TargetExecutionOutcome.LaunchFailed, EngineOutcome.Failed)]
    [InlineData(TargetExecutionOutcome.ReadinessTimedOut, EngineOutcome.Failed)]
    [InlineData(TargetExecutionOutcome.Blocked, EngineOutcome.NotStarted)]
    [InlineData(TargetExecutionOutcome.ReadinessUnknown, EngineOutcome.Unknown)]
    public void PreservesPrimaryFacts(TargetExecutionOutcome outcome, EngineOutcome expected)
    {
        var primary = new ExecutionStepFact("binding-fixture", EngineOutcome.NotStarted, BusinessOutcome.Unverified, "execution_primary_not_dispatched");
        var composed = TargetFactsPolicy.Compose(primary, new(outcome, "execution_target_observed"));
        Assert.Equal(primary, composed.Primary);
        Assert.Equal(expected, composed.Combined.Engine);
        Assert.Equal(BusinessOutcome.Unverified, composed.Combined.Business);
    }

    [Fact(DisplayName = "A Completed Main Keeps Its Accepted Business And Cannot Be Rewritten As An Earlier Target Failure")]
    public void KeepsAcceptedBusiness()
    {
        var primary = new ExecutionStepFact("binding-fixture", EngineOutcome.Failed, BusinessOutcome.Succeeded, "execution_nonzero_exit");
        var composed = TargetFactsPolicy.Compose(primary, new(TargetExecutionOutcome.OwnedReady, "execution_target_owned_ready"));
        Assert.Equal(EngineOutcome.Failed, composed.Combined.Engine);
        Assert.Equal(BusinessOutcome.Succeeded, composed.Combined.Business);
        Assert.Throws<ArgumentException>(() => TargetFactsPolicy.Compose(primary,
            new(TargetExecutionOutcome.ReadinessTimedOut, "execution_target_readiness_timeout")));
    }
    [Fact(DisplayName = "An Unresolved Registered Target Keeps Known Main Business But Cannot Become Overall Success")]
    public void RejectsUnresolvedSuccess()
    {
        var primary = new ExecutionStepFact("binding-fixture", EngineOutcome.Succeeded, BusinessOutcome.Succeeded, "execution_business_succeeded");
        var composed = TargetFactsPolicy.Compose(primary,
            new(TargetExecutionOutcome.UnverifiedDispatch, "execution_target_dispatch_unverified"));
        Assert.Equal(primary, composed.Primary);
        Assert.Equal(EngineOutcome.Unknown, composed.Combined.Engine);
        Assert.Equal(BusinessOutcome.Succeeded, composed.Combined.Business);
    }
}
