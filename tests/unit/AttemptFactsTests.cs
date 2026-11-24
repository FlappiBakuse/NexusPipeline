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

public sealed class HookFactsTests
{
    [Theory(DisplayName = "Hook Engine Composition Retains Primary Business And Uses Existing Precedence")]
    [InlineData(EngineOutcome.Succeeded, EngineOutcome.Failed, EngineOutcome.Failed, BusinessOutcome.Succeeded)]
    [InlineData(EngineOutcome.Failed, EngineOutcome.Succeeded, EngineOutcome.Failed, BusinessOutcome.Failed)]
    [InlineData(EngineOutcome.Succeeded, EngineOutcome.Unknown, EngineOutcome.Unknown, BusinessOutcome.Partial)]
    [InlineData(EngineOutcome.Cancelled, EngineOutcome.Succeeded, EngineOutcome.Cancelled, BusinessOutcome.Unverified)]
    [InlineData(EngineOutcome.NotStarted, EngineOutcome.Failed, EngineOutcome.Failed, BusinessOutcome.Unverified)]
    [InlineData(EngineOutcome.Succeeded, EngineOutcome.NotStarted, EngineOutcome.Succeeded, BusinessOutcome.NotApplicable)]
    public void PreservesPrimaryBusiness(EngineOutcome main, EngineOutcome hook,
        EngineOutcome combined, BusinessOutcome business)
    {
        var primary = new ExecutionStepFact("binding", main, business, "execution_primary_fact");
        var hookFact = new HookEngineFact(ExecutionHookRole.Post, hook, "execution_post_hook_fact");
        var result = HookFactsPolicy.Compose(primary, [hookFact]);
        Assert.Equal(primary, result.Primary); Assert.Equal(combined, result.Combined.Engine);
        Assert.Equal(business, result.Combined.Business); Assert.Equal("binding", result.Combined.StepId);
        Assert.Equal(hookFact, Assert.Single(result.Hooks));
    }

    [Fact(DisplayName = "A Post Hook Failure Uses Engine Failed And Does Not Invent A Failed Lifecycle")]
    public void SeparatesPostFailure()
    {
        var result = HookFactsPolicy.Compose(new("binding", EngineOutcome.Succeeded, BusinessOutcome.Succeeded, "execution_main_succeeded"),
            [new(ExecutionHookRole.Post, EngineOutcome.Failed, "execution_post_hook_failed")]);
        var facts = AttemptFactsPolicy.Aggregate(Guid.NewGuid(), Guid.NewGuid(), 1, [result.Combined],
            LifecycleOutcome.Completed, RecoveryOutcome.Restored, "execution_post_hook_failed");
        Assert.Equal(EngineOutcome.Failed, facts.Engine); Assert.Equal(BusinessOutcome.Succeeded, facts.Business);
        Assert.Equal(LifecycleOutcome.Completed, facts.Lifecycle); Assert.Equal(RecoveryOutcome.Restored, facts.Recovery);
        Assert.Equal(AttemptOutcome.Failed, RetryDecision.Classify(facts));
        Assert.Throws<ArgumentException>(() => AttemptFactsPolicy.Aggregate(Guid.NewGuid(), Guid.NewGuid(), 1,
            [result.Combined], LifecycleOutcome.Blocked, RecoveryOutcome.Restored, "execution_post_hook_failed"));
    }

    [Fact(DisplayName = "Duplicate Unordered Running And Unknown Hook Roles Are Rejected")]
    public void RejectsInvalidRoles()
    {
        var primary = new ExecutionStepFact("binding", EngineOutcome.Succeeded, BusinessOutcome.Succeeded, "execution_main_succeeded");
        Assert.Throws<ArgumentException>(() => HookFactsPolicy.Compose(primary,
            [new(ExecutionHookRole.Pre, EngineOutcome.Succeeded, "execution_pre_done"), new(ExecutionHookRole.Pre, EngineOutcome.Failed, "execution_pre_failed")]));
        Assert.Throws<ArgumentException>(() => HookFactsPolicy.Compose(primary,
            [new(ExecutionHookRole.Post, EngineOutcome.Succeeded, "execution_post_done"), new(ExecutionHookRole.Pre, EngineOutcome.Succeeded, "execution_pre_done")]));
        Assert.Throws<ArgumentException>(() => HookFactsPolicy.Compose(primary, [new(ExecutionHookRole.Pre, EngineOutcome.Running, "execution_pre_running")]));
        Assert.Throws<ArgumentException>(() => HookFactsPolicy.Compose(primary, [new((ExecutionHookRole)99, EngineOutcome.Succeeded, "execution_hook_unknown")]));
    }
}

public sealed class LauncherFactsTests
{
    [Theory(DisplayName = "A Launcher Launch Fact Preserves Main Business And The Main Reason Wins At Equal Severity")]
    [InlineData(EngineOutcome.Succeeded, EngineOutcome.Failed, EngineOutcome.Failed, false)]
    [InlineData(EngineOutcome.Failed, EngineOutcome.Failed, EngineOutcome.Failed, true)]
    [InlineData(EngineOutcome.Unknown, EngineOutcome.Succeeded, EngineOutcome.Unknown, true)]
    [InlineData(EngineOutcome.NotStarted, EngineOutcome.Unknown, EngineOutcome.Unknown, false)]
    [InlineData(EngineOutcome.Succeeded, EngineOutcome.Succeeded, EngineOutcome.Succeeded, true)]
    [InlineData(EngineOutcome.NotStarted, EngineOutcome.NotStarted, EngineOutcome.NotStarted, true)]
    public void PreservesMainBusiness(
        EngineOutcome main, EngineOutcome launcher, EngineOutcome expected, bool keepMainReason)
    {
        var primary = new ExecutionStepFact("binding-real", main, BusinessOutcome.Unverified, "primary_fact");
        var result = ProviderFactsPolicy.ComposeLauncher(primary, launcher, "launcher_fact");
        Assert.Equal(expected, result.Engine); Assert.Equal(primary.Business, result.Business);
        Assert.Equal(primary.StepId, result.StepId);
        Assert.Equal(keepMainReason ? "primary_fact" : "launcher_fact", result.ReasonCode);
    }

    [Theory(DisplayName = "An Unfinished Or Invented Launcher Outcome Cannot Become A Terminal Auxiliary Fact")]
    [InlineData(EngineOutcome.Running)]
    [InlineData(EngineOutcome.Cancelled)]
    [InlineData((EngineOutcome)99)]
    public void RejectsUnfinishedOutcomes(EngineOutcome launcher) =>
        Assert.Throws<ArgumentException>(() => ProviderFactsPolicy.ComposeLauncher(
            new("binding-real", EngineOutcome.NotStarted, BusinessOutcome.Unverified, "main_unstarted"), launcher, "launcher_fact"));
}

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
