using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class CompletionActionTests
{
    [Theory(DisplayName = "An Actual Completed Restored Business Failure Keeps Its Facts And The Configured Countdown")]
    [InlineData(AutomationCompletionAction.Shutdown, 60, true)]
    [InlineData(AutomationCompletionAction.Hibernate, 60, true)]
    [InlineData(AutomationCompletionAction.Restart, 60, true)]
    [InlineData(AutomationCompletionAction.Exit, 0, false)]
    public void PreservesFailedBusiness(
        AutomationCompletionAction action, int seconds, bool cancellable)
    {
        var (intent, receipt) = Example(action);
        var decision = CompletionActionPolicy.Evaluate(intent, receipt);
        Assert.True(decision.IsEligible); Assert.Equal(TimeSpan.FromSeconds(seconds), decision.Countdown);
        Assert.Equal(cancellable, CompletionActionPolicy.CanCancel(action));
        Assert.Equal(BusinessOutcome.Failed, receipt.Facts.Business);
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine);
    }

    [Theory(DisplayName = "Cancellation Or Unresolved Recovery Never Produces An Automatic System Action")]
    [InlineData(LifecycleOutcome.Cancelled, RecoveryOutcome.Restored)]
    [InlineData(LifecycleOutcome.Interrupted, RecoveryOutcome.Restored)]
    [InlineData(LifecycleOutcome.Blocked, RecoveryOutcome.NotRequired)]
    [InlineData(LifecycleOutcome.Completed, RecoveryOutcome.Pending)]
    [InlineData(LifecycleOutcome.Completed, RecoveryOutcome.Quarantined)]
    public void RejectsUnresolvedActions(LifecycleOutcome lifecycle, RecoveryOutcome recovery)
    {
        var (intent, receipt) = Example(AutomationCompletionAction.Shutdown);
        Assert.False(CompletionActionPolicy.Evaluate(intent, receipt with { Facts = receipt.Facts with { Lifecycle = lifecycle, Recovery = recovery } }).IsEligible);
    }

    [Fact(DisplayName = "Incompatible Actions Cannot Join The Same Closing Group")]
    public void RejectsIncompatibleActions()
    {
        var (intent, _) = Example(AutomationCompletionAction.Shutdown);
        var other = intent with { RunId = Guid.NewGuid(), Action = AutomationCompletionAction.Restart };
        Assert.Throws<InvalidOperationException>(() => CompletionActionPolicy.Combine([intent, other]));
    }

    [Fact(DisplayName = "Matching Actions Keep Each Immutable Run Intent Rather Than Collapsing Their Receipts")]
    public void PreservesRunIntents()
    {
        var (intent, _) = Example(AutomationCompletionAction.Hibernate);
        var other = intent with { RunId = Guid.NewGuid(), AttemptId = Guid.NewGuid() };
        var group = CompletionActionPolicy.Combine([intent, other]);
        Assert.Equal(AutomationCompletionAction.Hibernate, group.Action);
        Assert.Equal(2, group.Intents.Length); Assert.Equal(intent, group.Intents[0]); Assert.Equal(other, group.Intents[1]);
    }

    [Fact(DisplayName = "A Receipt From Another Attempt Cannot Authorize The Intent")]
    public void BindsAttemptReceipts()
    {
        var (intent, receipt) = Example(AutomationCompletionAction.Exit);
        Assert.Throws<ArgumentException>(() => CompletionActionPolicy.Evaluate(intent,
            receipt with { Facts = receipt.Facts with { AttemptId = Guid.NewGuid() } }));
    }

    private static (FrozenCompletionIntent Intent, AttemptReceipt Receipt) Example(AutomationCompletionAction action)
    {
        var run = Guid.NewGuid(); var attempt = Guid.NewGuid();
        var intent = new FrozenCompletionIntent(run, attempt, Guid.NewGuid(), 1, action, new('a', 64), new('b', 64));
        return (intent, new(Guid.NewGuid(), Guid.NewGuid(), intent.PlanDigest, intent.SourceReceiptDigest,
            new(run, attempt, 1, EngineOutcome.Failed, BusinessOutcome.Failed, LifecycleOutcome.Completed, RecoveryOutcome.Restored, "actual_failure")));
    }
}
