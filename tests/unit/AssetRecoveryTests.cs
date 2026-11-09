using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class AssetRecoveryTests
{
    [Fact(DisplayName = "An Unentered Asset Import With Complete No Effect Evidence Is Sealed And Cancelled Without Publishing Definitions")]
    public void CancelsUnenteredImports()
    {
        var session = new Session();
        Assert.Equal(JournalRecoveryResult.NotExecuted, new AutomationAssetRecovery(session).Recover());
        Assert.Equal(new[] { "no-effects" }, session.Events.Where(item => item != "seal"));
        Assert.Equal(EffectGateState.Sealed, session.State.Gate);
        Assert.Equal(JournalPhase.Committed, session.State.Phase);
    }

    [Fact(DisplayName = "An Entered Prepared Asset Import Cannot Be Resealed Or Turned Into An Assumed No Effect Commit")]
    public void RejectsEnteredResealing()
    {
        var session = new Session();
        session.State = session.State with { Gate = EffectGateState.Entered };
        Assert.Equal(JournalRecoveryResult.Quarantined, new AutomationAssetRecovery(session).Recover());
        Assert.Equal(new[] { "quarantine:automation_asset_effect_entry_observed" }, session.Events);
    }

    [Theory(DisplayName = "Asset Recovery Cannot Publish Or Cancel While The Recorded Writer Stop Is Unconfirmed")]
    [InlineData(false, WriterState.Unknown)]
    [InlineData(true, WriterState.Running)]
    public void RequiresStoppedWriter(bool complete, WriterState writers)
    {
        var session = new Session();
        session.State = session.State with { IsWriterObservationComplete = complete, Writers = writers };
        Assert.Equal(JournalRecoveryResult.Quarantined, new AutomationAssetRecovery(session).Recover());
        Assert.Equal(new[] { "quarantine:automation_asset_writer_stop_unconfirmed" }, session.Events);
    }

    [Fact(DisplayName = "Complete Verified Asset Preparation Can Publish Its Whole Definition Transaction After The Old Writer Stops")]
    public void PublishesVerifiedPreparation()
    {
        var session = new Session();
        session.State = session.State with { Phase = JournalPhase.Applying, Gate = EffectGateState.Entered, CanPublishCompleteDefinitions = true };
        Assert.Equal(JournalRecoveryResult.Completed, new AutomationAssetRecovery(session).Recover());
        Assert.Equal(new[] { "publish-complete-definitions" }, session.Events);
    }

    [Fact(DisplayName = "Partial Or Unproven Asset Preparation Remains Isolated Without Repeating Content Writes")]
    public void IsolatesPartialPreparation()
    {
        var session = new Session();
        session.State = session.State with { Phase = JournalPhase.Applying, Gate = EffectGateState.Entered };
        Assert.Equal(JournalRecoveryResult.Quarantined, new AutomationAssetRecovery(session).Recover());
        Assert.Equal(new[] { "quarantine:automation_asset_preparation_incomplete" }, session.Events);
    }

    [Theory(DisplayName = "A Recorded Asset Commit Is Either Already Closed Or Reconciled Using Verified Independent Evidence")]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconcilesCommittedImports(bool databaseAgrees)
    {
        var session = new Session();
        session.State = session.State with { Phase = JournalPhase.Committed, DatabaseAgrees = databaseAgrees, CanReconcileRecordedCommit = true };
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted, new AutomationAssetRecovery(session).Recover());
        Assert.Equal(databaseAgrees ? [] : new[] { "reconcile" }, session.Events);
    }

    [Fact(DisplayName = "A Recorded Asset Commit Without Reconciliation Evidence Cannot Write A Replacement Projection")]
    public void RequiresReconciliationEvidence()
    {
        var session = new Session();
        session.State = session.State with { Phase = JournalPhase.Committed, DatabaseAgrees = false };
        Assert.Throws<InvalidOperationException>(() => new AutomationAssetRecovery(session).Recover());
        Assert.Empty(session.Events);
    }

    [Fact(DisplayName = "An Unsupported Asset Recovery Phase Is Rejected Before Any Publication Or Quarantine Write")]
    public void RejectsUnsupportedPhases()
    {
        var session = new Session();
        session.State = session.State with { Phase = JournalPhase.Reverting };
        Assert.Throws<InvalidOperationException>(() => new AutomationAssetRecovery(session).Recover());
        Assert.Empty(session.Events);
    }

    private sealed class Session : IAssetRecoverySession
    {
        internal AssetRecoveryInspection State = new(JournalPhase.Prepared, true, true, WriterState.Stopped,
            EffectGateState.Unentered, true, false, false);
        internal List<string> Events { get; } = [];
        public AssetRecoveryInspection Inspect() => State;
        public void SealEffectEntry() { Events.Add("seal"); State = State with { Gate = EffectGateState.Sealed }; }
        public void CommitNoEffects()
        {
            Assert.Equal(EffectGateState.Sealed, State.Gate);
            Events.Add("no-effects"); State = State with { Phase = JournalPhase.Committed };
        }
        public void PublishCompleteDefinitions() { Events.Add("publish-complete-definitions"); State = State with { Phase = JournalPhase.Committed }; }
        public void ReconcileRecordedCommit() { Events.Add("reconcile"); State = State with { DatabaseAgrees = true }; }
        public void Quarantine(string code) { Events.Add("quarantine:" + code); State = State with { Phase = JournalPhase.Quarantined }; }
    }
}
