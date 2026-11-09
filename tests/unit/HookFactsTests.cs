using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

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
