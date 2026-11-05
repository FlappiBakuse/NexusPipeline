using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Configuration;

public enum ConfigurationRecoveryResult { NotExecuted, Restored, Quarantined, AlreadyCompleted }

public sealed record ConfigurationRecoveryInspection(JournalPhase Phase, bool DatabaseAgrees, bool IsWriterObservationComplete,
    WriterState Writers, bool CanProveNoEffects, bool CanRestore, bool IsOriginalRestored, bool CanReconcileRecordedCommit);

public interface IConfigurationRecoverySession
{
    ConfigurationRecoveryInspection Inspect();
    void CommitNoEffects();
    void BeginRestoration();
    void RetireCandidate();
    void RestoreOriginal();
    void CommitRestoration();
    void Quarantine(string reasonCode);
    void ReconcileRecordedCommit();
}

public sealed class ConfigurationRecovery(IConfigurationRecoverySession session)
{
    public ConfigurationRecoveryResult Recover(bool isExplicitQuarantineRecovery = false)
    {
        var inspection = session.Inspect();
        if (inspection.Phase == JournalPhase.Committed)
        {
            if (!inspection.DatabaseAgrees)
            {
                if (!inspection.IsWriterObservationComplete || inspection.Writers != WriterState.Stopped || !inspection.CanReconcileRecordedCommit)
                    throw new InvalidOperationException("committed_journal_requires_reconciliation");
                session.ReconcileRecordedCommit();
            }
            return ConfigurationRecoveryResult.AlreadyCompleted;
        }
        if (inspection.Phase == JournalPhase.Quarantined && !isExplicitQuarantineRecovery)
            return ConfigurationRecoveryResult.Quarantined;
        if (!inspection.IsWriterObservationComplete || inspection.Writers != WriterState.Stopped)
            return Quarantine("writer_stop_unconfirmed");
        if (inspection.Phase == JournalPhase.Prepared && inspection.CanProveNoEffects)
        {
            session.CommitNoEffects();
            return ConfigurationRecoveryResult.NotExecuted;
        }
        if (!inspection.CanRestore)
            return Quarantine("configuration_state_unverified");
        if (inspection.Phase != JournalPhase.Reverted)
        {
            session.BeginRestoration();
            session.RetireCandidate();
            session.RestoreOriginal();
        }
        else if (!inspection.IsOriginalRestored)
            return Quarantine("configuration_restoration_unverified");
        session.CommitRestoration();
        return ConfigurationRecoveryResult.Restored;
    }

    private ConfigurationRecoveryResult Quarantine(string reason)
    {
        session.Quarantine(reason);
        return ConfigurationRecoveryResult.Quarantined;
    }
}
