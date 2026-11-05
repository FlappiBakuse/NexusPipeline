using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class PreparedCompletionTests
{
    [Theory(DisplayName = "A Verified Pending Configuration Scope Retains Its Parent Without A No Effect Decision")]
    [InlineData(EffectGateState.Unentered)]
    [InlineData(EffectGateState.Sealed)]
    public void PreservesPendingParent(EffectGateState gate)
    {
        var session = new PendingSession { Inspection = Ready with { Gate = gate } };
        Assert.Equal(JournalRecoveryResult.PendingConfigurationCompletion, new PreparedAttemptRecovery(session).Recover());
        Assert.Equal(gate == EffectGateState.Unentered ? 1 : 0, session.Seals);
        Assert.Equal(1, session.Verifications); Assert.Equal(0, session.Commits); Assert.Equal(0, session.Quarantines);
        Assert.Equal(JournalPhase.Prepared, session.Inspection.Phase);
    }

    [Fact(DisplayName = "An Inspection Flag Without The Dedicated Evidence Capability Cannot Defer A Parent")]
    public void RequiresEvidenceCapability()
    {
        var session = new BasicSession { Inspection = Ready };
        Assert.Equal(JournalRecoveryResult.Quarantined, new PreparedAttemptRecovery(session).Recover());
        Assert.Equal(0, session.Commits); Assert.Equal(1, session.Quarantines);
    }

    [Theory(DisplayName = "Ineligible Parent Facts Do Not Call The Configuration Completion Evidence")]
    [InlineData("database", JournalRecoveryResult.Quarantined)]
    [InlineData("incomplete", JournalRecoveryResult.Quarantined)]
    [InlineData("running", JournalRecoveryResult.Quarantined)]
    [InlineData("entered", JournalRecoveryResult.Quarantined)]
    [InlineData("no-grant", JournalRecoveryResult.Quarantined)]
    [InlineData("quarantined", JournalRecoveryResult.Quarantined)]
    [InlineData("committed", JournalRecoveryResult.AlreadyCompleted)]
    public void RejectsIneligibleParents(string fault, JournalRecoveryResult expected)
    {
        var session = new PendingSession { Inspection = fault switch
        {
            "database" => Ready with { DatabaseAgrees = false },
            "incomplete" => Ready with { IsWriterObservationComplete = false },
            "running" => Ready with { Writers = WriterState.Running },
            "entered" => Ready with { Gate = EffectGateState.Entered },
            "no-grant" => Ready with { CanDeferConfigurationCompletion = false },
            "quarantined" => Ready with { Phase = JournalPhase.Quarantined },
            _ => Ready with { Phase = JournalPhase.Committed }
        } };
        Assert.Equal(expected, new PreparedAttemptRecovery(session).Recover());
        Assert.Equal(0, session.Verifications); Assert.Equal(0, session.Commits);
    }

    [Fact(DisplayName = "Failure To Revalidate The Pending Scope Cannot Issue A No Effect Decision Or Success")]
    public void RevalidatesPendingScope()
    {
        var session = new PendingSession { Inspection = Ready, FailVerification = true };
        Assert.Throws<IOException>(() => new PreparedAttemptRecovery(session).Recover());
        Assert.Equal(1, session.Verifications); Assert.Equal(0, session.Commits); Assert.Equal(0, session.Quarantines);
        Assert.Equal(JournalPhase.Prepared, session.Inspection.Phase);
    }

    [Fact(DisplayName = "A Changed Inspection After Sealing Cannot Use The Previous Pending Evidence")]
    public void RejectsChangedInspection()
    {
        var session = new PendingSession { Inspection = Ready with { Gate = EffectGateState.Unentered }, ChangeOnSeal = true };
        Assert.Equal(JournalRecoveryResult.Quarantined, new PreparedAttemptRecovery(session).Recover());
        Assert.Equal(1, session.Seals); Assert.Equal(0, session.Verifications); Assert.Equal(0, session.Commits);
    }

    [Fact(DisplayName = "A Regular Prepared Attempt Keeps Its Existing No Effect Recovery Without The New Capability")]
    public void PreservesUnenteredRecovery()
    {
        var session = new BasicSession { Inspection = Ready with { CanDeferConfigurationCompletion = false, CanProveNoEffects = true } };
        Assert.Equal(JournalRecoveryResult.NotExecuted, new PreparedAttemptRecovery(session).Recover());
        Assert.Equal(1, session.Commits); Assert.Equal(0, session.Quarantines);
    }

    private static PreparedAttemptInspection Ready => new(JournalPhase.Prepared, true, EffectGateState.Sealed, true,
        WriterState.Stopped, false, false) { CanDeferConfigurationCompletion = true };

    // This fixture exercises the Core evidence port; it does not represent SQL or native stopping evidence.
    private class BasicSession : IPreparedRecoverySession
    {
        internal PreparedAttemptInspection Inspection { get; set; } = Ready;
        internal int Seals { get; private set; }
        internal int Commits { get; private set; }
        internal int Quarantines { get; private set; }
        internal bool ChangeOnSeal { get; init; }
        public PreparedAttemptInspection Inspect() => Inspection;
        public void SealEffectEntry()
        {
            Seals++; Inspection = Inspection with { Gate = EffectGateState.Sealed, DatabaseAgrees = !ChangeOnSeal };
        }
        public void CommitNoEffects() => Commits++;
        public void Quarantine(string reasonCode) => Quarantines++;
        public void ReconcileRecordedCommit() => throw new InvalidOperationException("unexpected_reconciliation");
    }

    private sealed class PendingSession : BasicSession, IPreparedConfigurationCompletion
    {
        internal int Verifications { get; private set; }
        internal bool FailVerification { get; init; }
        public void VerifyPendingCompletion()
        {
            Verifications++;
            if (FailVerification) throw new IOException("pending_evidence_changed");
        }
    }
}
