using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Secrets;

public enum PreparationRecoveryAction { CancelNoEffects, PublishCandidate, ReconcileCommit, AlreadyCompleted, Quarantine }
public sealed record PreparationRecoveryEvidence(JournalPhase Phase, bool DatabaseAgrees, bool WriterObservationComplete,
    WriterState OldWriters, bool GateEntered, bool NoEffectsProven, bool BeforeReferenceMatches,
    bool CandidateVerified, bool CandidateReceiptComplete, bool PublishedResultMatchesPlan);

public static class SecretPreparationRecovery
{
    public static PreparationRecoveryAction Decide(PreparationRecoveryEvidence evidence)
    {
        if (evidence.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Committed or JournalPhase.Quarantined))
            throw new InvalidOperationException("secret_preparation_phase_unrecognized");
        if (evidence.Phase == JournalPhase.Committed && evidence.DatabaseAgrees && evidence.PublishedResultMatchesPlan)
            return PreparationRecoveryAction.AlreadyCompleted;
        if (evidence.Phase == JournalPhase.Committed && (evidence.DatabaseAgrees || evidence.PublishedResultMatchesPlan))
            throw new InvalidOperationException("secret_preparation_commit_unverified");
        if (evidence.Phase != JournalPhase.Committed && evidence.PublishedResultMatchesPlan)
            throw new InvalidOperationException("secret_preparation_publication_phase_unbound");
        if (evidence.Phase == JournalPhase.Committed && (!evidence.WriterObservationComplete || evidence.OldWriters != WriterState.Stopped))
            throw new InvalidOperationException("secret_preparation_commit_unverified");
        if (!evidence.WriterObservationComplete || evidence.OldWriters != WriterState.Stopped)
            return PreparationRecoveryAction.Quarantine;
        var canPublish = evidence.GateEntered && evidence.BeforeReferenceMatches && evidence.CandidateVerified && evidence.CandidateReceiptComplete;
        if (evidence.Phase == JournalPhase.Committed)
            return canPublish ? PreparationRecoveryAction.ReconcileCommit : throw new InvalidOperationException("secret_preparation_commit_unverified");
        if (evidence.Phase == JournalPhase.Quarantined) return PreparationRecoveryAction.Quarantine;
        if (evidence.Phase == JournalPhase.Prepared)
            return !evidence.GateEntered && evidence.NoEffectsProven && evidence.BeforeReferenceMatches && !evidence.CandidateVerified && !evidence.CandidateReceiptComplete
                ? PreparationRecoveryAction.CancelNoEffects : PreparationRecoveryAction.Quarantine;
        return canPublish ? PreparationRecoveryAction.PublishCandidate : PreparationRecoveryAction.Quarantine;
    }
}
