using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class CleanupRecoveryTests
{
    [Fact(DisplayName = "Prepared Cleanup Recovery Requires Positive No Effect Evidence And Does Not Start Deletion")]
    public void RequiresUnenteredProof()
    {
        var session = new Session(Inspect(JournalPhase.Prepared));
        Assert.Equal(FileCleanupResult.NotExecuted, new FileCleanupRecovery(session).Recover());
        Assert.Equal(["not-executed"], session.Events);
        session.Events.Clear();
        session.Inspection = session.Inspection with { CanProveNoEffects = false };
        Assert.Equal(FileCleanupResult.Quarantined, new FileCleanupRecovery(session).Recover());
        Assert.Equal(["quarantine:cleanup_prepared_no_effect_unverified"], session.Events);
    }

    [Fact(DisplayName = "Normal Cleanup Only Deletes Verified Unreferenced Items And Retains Referenced Objects")]
    public void DeletesUnreferencedItems()
    {
        var session = new Session(Inspect(JournalPhase.Prepared) with
        {
            Items = [new("one", CleanupItemState.VerifiedUnreferenced), new("two", CleanupItemState.Referenced)]
        });
        Assert.Equal(FileCleanupResult.Cleaned, new FileCleanupRecovery(session).Execute());
        Assert.Equal(["applying:False", "remove:one", "skip:two", "applied", "committed"], session.Events);
    }

    [Fact(DisplayName = "Unknown Later Cleanup Items Prevent Every Earlier Deletion")]
    public void RejectsUnknownItems()
    {
        var session = new Session(Inspect(JournalPhase.Applying) with
        {
            Items = [new("one", CleanupItemState.VerifiedUnreferenced), new("two", CleanupItemState.Unknown)]
        });
        Assert.Equal(FileCleanupResult.Quarantined, new FileCleanupRecovery(session).Recover());
        Assert.Equal(["quarantine:cleanup_inventory_unverified"], session.Events);
    }

    [Fact(DisplayName = "Recovery Keeps Recorded Deletions And Rechecks New References Before Removing Remaining Objects")]
    public void RechecksRecoveredReferences()
    {
        var session = new Session(Inspect(JournalPhase.Applying) with
        {
            Items = [new("one", CleanupItemState.DeletedWithEvidence), new("two", CleanupItemState.VerifiedUnreferenced),
                new("three", CleanupItemState.SkippedWithEvidence)]
        }) { Result = CleanupStepResult.SkippedReferenced };
        Assert.Equal(FileCleanupResult.Cleaned, new FileCleanupRecovery(session).Recover());
        Assert.Equal(["applying:False", "remove:two", "skip:two", "applied", "committed"], session.Events);
    }

    [Fact(DisplayName = "An Unverified Deletion Stops Later Items And Cannot Be Reported As Committed")]
    public void RejectsUnverifiedDeletion()
    {
        var session = new Session(Inspect(JournalPhase.Prepared) with
        {
            Items = [new("one", CleanupItemState.VerifiedUnreferenced), new("two", CleanupItemState.VerifiedUnreferenced)]
        }) { Result = CleanupStepResult.Unknown };
        Assert.Equal(FileCleanupResult.Quarantined, new FileCleanupRecovery(session).Execute());
        Assert.Equal(["applying:False", "remove:one", "quarantine:cleanup_effect_unverified"], session.Events);
    }

    [Theory(DisplayName = "Neither Cleanup Execution Nor Recovery May Delete While Writers Are Unconfirmed")]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresStoppedWriters(bool recover)
    {
        var session = new Session(Inspect(JournalPhase.Prepared) with { IsWriterObservationComplete = false, Writers = WriterState.Unknown });
        var cleanup = new FileCleanupRecovery(session);
        Assert.Equal(FileCleanupResult.Quarantined, recover ? cleanup.Recover() : cleanup.Execute());
        Assert.Equal(["quarantine:cleanup_writer_stop_unconfirmed"], session.Events);
    }

    [Fact(DisplayName = "Quarantined Cleanup Requires An Explicit Verified Recovery And Never Attempts Rollback")]
    public void RequiresExplicitRecovery()
    {
        var session = new Session(Inspect(JournalPhase.Quarantined));
        var cleanup = new FileCleanupRecovery(session);
        Assert.Equal(FileCleanupResult.Quarantined, cleanup.Recover());
        Assert.Empty(session.Events);
        Assert.Equal(FileCleanupResult.Cleaned, cleanup.Recover(isExplicitQuarantineRecovery: true));
        Assert.Equal(["applying:True", "remove:one", "applied", "committed"], session.Events);
    }

    [Fact(DisplayName = "An Applied Cleanup Can Only Commit After All Items Have An Explained Terminal State")]
    public void RequiresTerminalItems()
    {
        var session = new Session(Inspect(JournalPhase.Applied));
        Assert.Equal(FileCleanupResult.Quarantined, new FileCleanupRecovery(session).Recover());
        Assert.Equal(["quarantine:cleanup_applied_items_incomplete"], session.Events);
        session.Events.Clear();
        session.Inspection = session.Inspection with { Items = [new("one", CleanupItemState.DeletedWithEvidence)] };
        Assert.Equal(FileCleanupResult.Cleaned, new FileCleanupRecovery(session).Recover());
        Assert.Equal(["committed"], session.Events);
    }

    [Fact(DisplayName = "A Recorded Cleanup Commit Is Reconciled Without Repeating Physical Deletion")]
    public void KeepsCommittedDeletions()
    {
        var session = new Session(Inspect(JournalPhase.Committed) with { DatabaseAgrees = false, CanReconcileRecordedCommit = true });
        Assert.Equal(FileCleanupResult.AlreadyCompleted, new FileCleanupRecovery(session).Recover());
        Assert.Equal(["reconcile"], session.Events);
        session.Events.Clear();
        session.Inspection = session.Inspection with { CanReconcileRecordedCommit = false };
        Assert.Throws<InvalidOperationException>(() => new FileCleanupRecovery(session).Recover());
        Assert.Empty(session.Events);
    }

    private static FileCleanupInspection Inspect(JournalPhase phase) => new(phase, true, true, WriterState.Stopped, true, false,
        [new("one", CleanupItemState.VerifiedUnreferenced)]);

    private sealed class Session(FileCleanupInspection inspection) : ICleanupRecoverySession
    {
        public FileCleanupInspection Inspection { get; set; } = inspection;
        public CleanupStepResult Result { get; init; } = CleanupStepResult.Deleted;
        public List<string> Events { get; } = [];
        public FileCleanupInspection Inspect() => Inspection;
        public void CommitNoEffects() => Events.Add("not-executed");
        public void BeginApplying(bool hasVerifiedRecoveryEvidence) => Events.Add("applying:" + hasVerifiedRecoveryEvidence);
        public CleanupStepResult RemoveVerified(string stepId) { Events.Add("remove:" + stepId); return Result; }
        public void RecordReferencedSkip(string stepId) => Events.Add("skip:" + stepId);
        public void MarkApplied() => Events.Add("applied");
        public void CommitCleanup() => Events.Add("committed");
        public void Quarantine(string reasonCode) => Events.Add("quarantine:" + reasonCode);
        public void ReconcileRecordedCommit() => Events.Add("reconcile");
    }
}
