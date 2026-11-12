using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Configuration;

public enum CleanupItemState { VerifiedUnreferenced, DeletedWithEvidence, SkippedWithEvidence, Referenced, Unknown }
public enum CleanupStepResult { Deleted, SkippedReferenced, Unknown }
public enum FileCleanupResult { NotExecuted, Cleaned, Quarantined, AlreadyCompleted }
public sealed record CleanupItemInspection(string StepId, CleanupItemState State);
public sealed record FileCleanupInspection(JournalPhase Phase, bool DatabaseAgrees, bool IsWriterObservationComplete,
    WriterState Writers, bool CanProveNoEffects, bool CanReconcileRecordedCommit, IReadOnlyList<CleanupItemInspection> Items);

public interface ICleanupRecoverySession
{
    FileCleanupInspection Inspect();
    FileCleanupInspection InspectRecovery() => Inspect();
    void CommitNoEffects();
    void BeginApplying(bool hasVerifiedRecoveryEvidence);
    CleanupStepResult RemoveVerified(string stepId);
    void RecordReferencedSkip(string stepId);
    void MarkApplied();
    void CommitCleanup();
    void Quarantine(string reasonCode);
    void ReconcileRecordedCommit();
}

public sealed class FileCleanupRecovery(ICleanupRecoverySession session)
{
    public FileCleanupResult Execute()
    {
        var inspection = Freeze(session.Inspect());
        if (inspection.Phase != JournalPhase.Prepared || !inspection.DatabaseAgrees)
            throw new InvalidOperationException("cleanup_execution_not_prepared");
        if (!WritersStopped(inspection)) return Quarantine("cleanup_writer_stop_unconfirmed");
        if (!inspection.CanProveNoEffects || inspection.Items.Any(item => item.State is CleanupItemState.Unknown or CleanupItemState.DeletedWithEvidence or CleanupItemState.SkippedWithEvidence))
            return Quarantine("cleanup_initial_inventory_unverified");
        session.BeginApplying(hasVerifiedRecoveryEvidence: false);
        return Finish(inspection.Items);
    }

    public FileCleanupResult Recover(bool isExplicitQuarantineRecovery = false)
    {
        var inspection = Freeze(session.InspectRecovery());
        if (inspection.Phase == JournalPhase.Committed)
        {
            if (!inspection.DatabaseAgrees)
            {
                if (!WritersStopped(inspection) || !inspection.CanReconcileRecordedCommit)
                    throw new InvalidOperationException("cleanup_recorded_commit_unverified");
                session.ReconcileRecordedCommit();
            }
            return FileCleanupResult.AlreadyCompleted;
        }
        if (inspection.Phase == JournalPhase.Quarantined && !isExplicitQuarantineRecovery) return FileCleanupResult.Quarantined;
        if (!WritersStopped(inspection)) return Quarantine("cleanup_writer_stop_unconfirmed");
        if (inspection.Phase == JournalPhase.Prepared)
        {
            if (!inspection.CanProveNoEffects || inspection.Items.Any(item => item.State is CleanupItemState.Unknown or CleanupItemState.DeletedWithEvidence))
                return Quarantine("cleanup_prepared_no_effect_unverified");
            session.CommitNoEffects();
            return FileCleanupResult.NotExecuted;
        }
        if (inspection.Items.Any(item => item.State == CleanupItemState.Unknown)) return Quarantine("cleanup_inventory_unverified");
        if (inspection.Phase == JournalPhase.Applied)
        {
            if (inspection.Items.Any(item => item.State is not (CleanupItemState.DeletedWithEvidence or CleanupItemState.SkippedWithEvidence)))
                return Quarantine("cleanup_applied_items_incomplete");
            session.CommitCleanup();
            return FileCleanupResult.Cleaned;
        }
        if (inspection.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined))
            throw new InvalidOperationException("cleanup_recovery_phase_invalid");
        session.BeginApplying(hasVerifiedRecoveryEvidence: inspection.Phase == JournalPhase.Quarantined);
        return Finish(inspection.Items);
    }

    private FileCleanupResult Finish(IReadOnlyList<CleanupItemInspection> items)
    {
        foreach (var item in items)
        {
            if (item.State is CleanupItemState.DeletedWithEvidence or CleanupItemState.SkippedWithEvidence) continue;
            if (item.State == CleanupItemState.Referenced) { session.RecordReferencedSkip(item.StepId); continue; }
            var result = session.RemoveVerified(item.StepId);
            if (result == CleanupStepResult.Unknown) return Quarantine("cleanup_effect_unverified");
            if (result == CleanupStepResult.SkippedReferenced) session.RecordReferencedSkip(item.StepId);
            else if (result != CleanupStepResult.Deleted) throw new InvalidOperationException("cleanup_step_result_invalid");
        }
        session.MarkApplied();
        session.CommitCleanup();
        return FileCleanupResult.Cleaned;
    }

    private static FileCleanupInspection Freeze(FileCleanupInspection inspection)
    {
        var items = Array.AsReadOnly(inspection.Items.ToArray());
        if (!Enum.IsDefined(inspection.Phase) || items.Count is < 1 or > 256 ||
            items.Any(item => string.IsNullOrWhiteSpace(item.StepId) || item.StepId.Length > 80 || !Enum.IsDefined(item.State)) ||
            items.Select(item => item.StepId).Distinct(StringComparer.Ordinal).Count() != items.Count)
            throw new InvalidOperationException("cleanup_inspection_invalid");
        return inspection with { Items = items };
    }

    private static bool WritersStopped(FileCleanupInspection inspection) => inspection.IsWriterObservationComplete && inspection.Writers == WriterState.Stopped;
    private FileCleanupResult Quarantine(string reason) { session.Quarantine(reason); return FileCleanupResult.Quarantined; }
}
