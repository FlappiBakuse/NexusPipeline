using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Configuration;

public sealed record PreparedPayloadInspection(JournalPhase Phase, bool DatabaseAgrees, EffectGateState Gate,
    bool IsWriterObservationComplete, WriterState Writers, bool CanProveNoEffects, bool CanReconcileRecordedCommit);

public interface IPayloadRecoverySession
{
    PreparedPayloadInspection Inspect();
    void SealEffectEntry();
    void CommitNoEffects();
    void Quarantine(string reasonCode);
    void ReconcileRecordedCommit();
}

public sealed class PreparedPayloadRecovery(IPayloadRecoverySession session)
{
    public JournalRecoveryResult Recover()
    {
        var inspected = session.Inspect();
        if (inspected.Phase == JournalPhase.Committed)
        {
            if (!inspected.DatabaseAgrees)
            {
                if (!Stopped(inspected) || inspected.Gate != EffectGateState.Sealed || !inspected.CanReconcileRecordedCommit)
                    throw new InvalidOperationException("payload_recorded_commit_unverified");
                session.ReconcileRecordedCommit();
            }
            return JournalRecoveryResult.AlreadyCompleted;
        }
        if (inspected.Phase == JournalPhase.Quarantined) return JournalRecoveryResult.Quarantined;
        if (inspected.Phase != JournalPhase.Prepared) throw new InvalidOperationException("payload_nonprepared_recovery_unavailable");
        if (!Stopped(inspected)) return Quarantine("payload_writer_stop_unconfirmed");
        if (inspected.Gate == EffectGateState.Entered) return Quarantine("payload_effect_entry_observed");
        if (inspected.Gate == EffectGateState.Unentered) session.SealEffectEntry();
        inspected = session.Inspect();
        if (!Stopped(inspected) || inspected.Gate != EffectGateState.Sealed || !inspected.CanProveNoEffects)
            return Quarantine("payload_prepared_no_effect_unverified");
        session.CommitNoEffects();
        return JournalRecoveryResult.NotExecuted;
    }

    private static bool Stopped(PreparedPayloadInspection inspection) => inspection.IsWriterObservationComplete && inspection.Writers == WriterState.Stopped;
    private JournalRecoveryResult Quarantine(string reason) { session.Quarantine(reason); return JournalRecoveryResult.Quarantined; }
}
