using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Core.Execution;

public sealed record PreparedAttemptInspection(JournalPhase Phase, bool DatabaseAgrees, EffectGateState Gate,
    bool IsWriterObservationComplete, WriterState Writers, bool CanProveNoEffects, bool CanReconcileRecordedCommit)
{
    public bool CanDeferConfigurationCompletion { get; init; }
}

public interface IPreparedConfigurationCompletion
{
    void VerifyPendingCompletion();
}

public interface IPreparedRecoverySession
{
    PreparedAttemptInspection Inspect();
    void SealEffectEntry();
    void CommitNoEffects();
    void Quarantine(string reasonCode);
    void ReconcileRecordedCommit();
}

public sealed class PreparedAttemptRecovery(IPreparedRecoverySession session)
{
    public JournalRecoveryResult Recover()
    {
        var inspected = session.Inspect();
        if (inspected.Phase == JournalPhase.Committed)
        {
            if (!inspected.DatabaseAgrees)
            {
                if (!Stopped(inspected) || inspected.Gate != EffectGateState.Sealed || !inspected.CanReconcileRecordedCommit)
                    throw new InvalidOperationException("attempt_recorded_commit_unverified");
                session.ReconcileRecordedCommit();
            }
            return JournalRecoveryResult.AlreadyCompleted;
        }
        if (inspected.Phase == JournalPhase.Quarantined) return JournalRecoveryResult.Quarantined;
        if (inspected.Phase != JournalPhase.Prepared) throw new InvalidOperationException("attempt_nonprepared_recovery_unavailable");
        if (!Stopped(inspected)) return Quarantine("attempt_writer_stop_unconfirmed");
        if (inspected.Gate == EffectGateState.Entered) return Quarantine("attempt_effect_entry_observed");
        if (inspected.Gate == EffectGateState.Unentered) session.SealEffectEntry();
        inspected = session.Inspect();
        if (inspected.DatabaseAgrees && Stopped(inspected) && inspected.Phase == JournalPhase.Prepared &&
            inspected.Gate == EffectGateState.Sealed && inspected.CanDeferConfigurationCompletion &&
            session is IPreparedConfigurationCompletion pending)
        {
            pending.VerifyPendingCompletion();
            return JournalRecoveryResult.PendingConfigurationCompletion;
        }
        if (!Stopped(inspected) || inspected.Gate != EffectGateState.Sealed || !inspected.CanProveNoEffects)
            return Quarantine("attempt_prepared_no_effect_unverified");
        session.CommitNoEffects();
        return JournalRecoveryResult.NotExecuted;
    }

    private static bool Stopped(PreparedAttemptInspection inspection) => inspection.IsWriterObservationComplete && inspection.Writers == WriterState.Stopped;
    private JournalRecoveryResult Quarantine(string reason) { session.Quarantine(reason); return JournalRecoveryResult.Quarantined; }
}
