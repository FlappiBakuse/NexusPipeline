using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class RecoveryRulesTests
{
    [Theory(DisplayName = "Prepared Termination Requires Positive Complete No Effect Evidence")]
    [InlineData(JournalKind.ConfigExchange)]
    [InlineData(JournalKind.ConfigEdit)]
    [InlineData(JournalKind.FileCleanup)]
    [InlineData(JournalKind.RunAttempt)]
    [InlineData(JournalKind.UpdateApply)]
    [InlineData(JournalKind.ExtensionApply)]
    [InlineData(JournalKind.AssetPrepare)]
    [InlineData(JournalKind.SecretPrepare)]
    public void RequiresUnenteredEvidence(JournalKind kind)
    {
        Assert.False(JournalTransitionPolicy.CanMove(kind, JournalPhase.Prepared, JournalPhase.Committed));
        var proof = new NoEffectEvidence(true, false, false, false);
        Assert.True(JournalTransitionPolicy.CanMove(kind, JournalPhase.Prepared, JournalPhase.Committed, proof));
        Assert.False(JournalTransitionPolicy.CanMove(kind, JournalPhase.Prepared, JournalPhase.Committed, proof with { HasStartedWriter = true }));
        Assert.False(JournalTransitionPolicy.CanMove(kind, JournalPhase.Prepared, JournalPhase.Committed, proof with { HasExternalWrites = true }));
        Assert.False(JournalTransitionPolicy.CanMove(kind, JournalPhase.Prepared, JournalPhase.Committed, proof with { HasUnknownBytes = true }));
        Assert.False(JournalTransitionPolicy.CanMove(kind, JournalPhase.Prepared, JournalPhase.Committed, proof with { HasCompleteInventory = false }));
    }

    [Fact(DisplayName = "Cleanup Cannot Pretend To Rollback And Configuration Cannot Commit Before Restoration")]
    public void RequiresRestoredEffects()
    {
        Assert.False(JournalTransitionPolicy.CanMove(JournalKind.FileCleanup, JournalPhase.Applied, JournalPhase.Reverting));
        Assert.False(JournalTransitionPolicy.CanMove(JournalKind.ConfigExchange, JournalPhase.Applied, JournalPhase.Committed));
        Assert.True(JournalTransitionPolicy.CanMove(JournalKind.ConfigExchange, JournalPhase.Applied, JournalPhase.Reverting));
        Assert.True(JournalTransitionPolicy.CanMove(JournalKind.ConfigExchange, JournalPhase.Reverted, JournalPhase.Committed));
        Assert.False(JournalTransitionPolicy.CanMove(JournalKind.RunAttempt, JournalPhase.Committed, JournalPhase.Applying));
    }

    [Fact(DisplayName = "Quarantined Resources Require Verified Evidence And Their Specific Recovery Path")]
    public void RequiresExplicitRecovery()
    {
        Assert.False(JournalTransitionPolicy.CanMove(JournalKind.ConfigExchange, JournalPhase.Quarantined, JournalPhase.Reverting));
        Assert.True(JournalTransitionPolicy.CanMove(JournalKind.ConfigExchange, JournalPhase.Quarantined, JournalPhase.Reverting, hasVerifiedRecoveryEvidence: true));
        Assert.False(JournalTransitionPolicy.CanMove(JournalKind.ConfigExchange, JournalPhase.Quarantined, JournalPhase.Applying, hasVerifiedRecoveryEvidence: true));
        Assert.True(JournalTransitionPolicy.CanMove(JournalKind.FileCleanup, JournalPhase.Quarantined, JournalPhase.Applying, hasVerifiedRecoveryEvidence: true));
        Assert.False(JournalTransitionPolicy.CanMove((JournalKind)99, JournalPhase.Prepared, JournalPhase.Applying));
    }

    [Fact(DisplayName = "Interruption Does Not Invent Business Success Or Restore While Writers Are Unknown")]
    public void PreservesUnknownWriters()
    {
        var facts = new AttemptFacts(Guid.NewGuid(), Guid.NewGuid(), 1, EngineOutcome.Running,
            BusinessOutcome.Unverified, LifecycleOutcome.Running, RecoveryOutcome.Pending, "running");
        var blocked = facts.AfterInterruption(WriterState.Unknown, true);
        Assert.Equal(RecoveryOutcome.Quarantined, blocked.Recovery);
        Assert.Equal(LifecycleOutcome.Running, blocked.Lifecycle);
        var restored = facts.AfterInterruption(WriterState.Stopped, true);
        Assert.Equal(EngineOutcome.Unknown, restored.Engine);
        Assert.Equal(BusinessOutcome.Unverified, restored.Business);
        Assert.Equal(LifecycleOutcome.Interrupted, restored.Lifecycle);
        Assert.Equal(RecoveryOutcome.Restored, restored.Recovery);
        Assert.Equal(facts.AttemptId, restored.AttemptId);
        Assert.Throws<InvalidOperationException>(() => restored.AfterInterruption(WriterState.Stopped, true));
    }
}
