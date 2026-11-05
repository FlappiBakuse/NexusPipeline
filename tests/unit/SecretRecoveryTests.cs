using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Secrets;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class SecretRecoveryTests
{
    [Fact(DisplayName = "A Positively Unentered Secret Preparation Can Cancel Without Changing The Prior Field")]
    public void CancelsUnenteredPreparation()
    {
        var evidence = BeforeEffects();
        Assert.Equal(PreparationRecoveryAction.CancelNoEffects, SecretPreparationRecovery.Decide(evidence));
        Assert.Equal(PreparationRecoveryAction.Quarantine, SecretPreparationRecovery.Decide(evidence with { GateEntered = true }));
        Assert.Equal(PreparationRecoveryAction.Quarantine, SecretPreparationRecovery.Decide(evidence with { BeforeReferenceMatches = false }));
        Assert.Equal(PreparationRecoveryAction.Quarantine, SecretPreparationRecovery.Decide(evidence with { CandidateVerified = true }));
    }

    [Theory(DisplayName = "A Verified Secret Candidate Cannot Publish Before Every Old Writer Is Confirmed Stopped")]
    [InlineData(WriterState.Running, true)]
    [InlineData(WriterState.Unknown, false)]
    [InlineData(WriterState.Stopped, false)]
    public void RequiresStoppedWriters(WriterState state, bool complete)
    {
        var evidence = Candidate() with { OldWriters = state, WriterObservationComplete = complete };
        Assert.Equal(PreparationRecoveryAction.Quarantine, SecretPreparationRecovery.Decide(evidence));
    }

    [Theory(DisplayName = "A Complete Secret Candidate And Independent Receipt Can Publish Only Against Its Original Field Revision")]
    [InlineData(JournalPhase.Applying)]
    [InlineData(JournalPhase.Applied)]
    public void BindsOriginalRevision(JournalPhase phase)
    {
        var evidence = Candidate() with { Phase = phase };
        Assert.Equal(PreparationRecoveryAction.PublishCandidate, SecretPreparationRecovery.Decide(evidence));
        Assert.Equal(PreparationRecoveryAction.Quarantine, SecretPreparationRecovery.Decide(evidence with { BeforeReferenceMatches = false }));
        Assert.Equal(PreparationRecoveryAction.Quarantine, SecretPreparationRecovery.Decide(evidence with { CandidateReceiptComplete = false }));
        Assert.Equal(PreparationRecoveryAction.Quarantine, SecretPreparationRecovery.Decide(evidence with { CandidateVerified = false }));
    }

    [Fact(DisplayName = "A Recorded Secret Commit Needs Complete Independent Evidence Before Its Missing Sql Publication Can Be Reconciled")]
    public void RequiresCommitEvidence()
    {
        var evidence = Candidate() with { Phase = JournalPhase.Committed, DatabaseAgrees = false };
        Assert.Equal(PreparationRecoveryAction.ReconcileCommit, SecretPreparationRecovery.Decide(evidence));
        Assert.Throws<InvalidOperationException>(() => SecretPreparationRecovery.Decide(evidence with { CandidateReceiptComplete = false }));
        Assert.Throws<InvalidOperationException>(() => SecretPreparationRecovery.Decide(evidence with { OldWriters = WriterState.Unknown, WriterObservationComplete = false }));
        Assert.Throws<InvalidOperationException>(() => SecretPreparationRecovery.Decide(evidence with { DatabaseAgrees = true }));
    }

    [Fact(DisplayName = "A Closed Historical Secret Operation Does Not Recreate Ciphertext That Was Later Retired")]
    public void PreservesRetiredAbsence()
    {
        var evidence = BeforeEffects() with { Phase = JournalPhase.Committed, PublishedResultMatchesPlan = true };
        Assert.Equal(PreparationRecoveryAction.AlreadyCompleted, SecretPreparationRecovery.Decide(evidence));
    }

    private static PreparationRecoveryEvidence BeforeEffects() => new(JournalPhase.Prepared, true, true,
        WriterState.Stopped, false, true, true, false, false, false);
    private static PreparationRecoveryEvidence Candidate() => BeforeEffects() with
        { Phase = JournalPhase.Applying, GateEntered = true, NoEffectsProven = false, CandidateVerified = true, CandidateReceiptComplete = true };
}
