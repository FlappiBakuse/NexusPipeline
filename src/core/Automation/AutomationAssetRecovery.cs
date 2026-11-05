using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Automation;

public sealed record AssetRecoveryInspection(JournalPhase Phase, bool DatabaseAgrees,
    bool IsWriterObservationComplete, WriterState Writers, EffectGateState Gate,
    bool HasCompletePreparedNoEffectEvidence, bool CanPublishCompleteDefinitions, bool CanReconcileRecordedCommit);

public interface IAssetRecoverySession
{
    AssetRecoveryInspection Inspect();
    void SealEffectEntry();
    void CommitNoEffects();
    void PublishCompleteDefinitions();
    void ReconcileRecordedCommit();
    void Quarantine(string reasonCode);
}

public sealed class AutomationAssetRecovery(IAssetRecoverySession session)
{
    public JournalRecoveryResult Recover()
    {
        var state = session.Inspect();
        if (state.Phase == JournalPhase.Committed)
        {
            if (!state.DatabaseAgrees)
            {
                if (!Stopped(state) || !state.CanReconcileRecordedCommit)
                    throw new InvalidOperationException("automation_asset_recorded_commit_unverified");
                session.ReconcileRecordedCommit();
            }
            return JournalRecoveryResult.AlreadyCompleted;
        }
        if (state.Phase == JournalPhase.Quarantined) return JournalRecoveryResult.Quarantined;
        if (state.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied))
            throw new InvalidOperationException("automation_asset_recovery_phase_invalid");
        if (!Stopped(state)) return Quarantine("automation_asset_writer_stop_unconfirmed");
        if (state.Phase == JournalPhase.Prepared)
        {
            if (state.Gate == EffectGateState.Entered) return Quarantine("automation_asset_effect_entry_observed");
            if (state.Gate == EffectGateState.Unentered) session.SealEffectEntry();
            state = session.Inspect();
            if (!Stopped(state) || state.Gate != EffectGateState.Sealed || !state.HasCompletePreparedNoEffectEvidence)
                return Quarantine("automation_asset_prepared_evidence_unverified");
            session.CommitNoEffects();
            return JournalRecoveryResult.NotExecuted;
        }
        if (state.Gate != EffectGateState.Entered || !state.CanPublishCompleteDefinitions)
            return Quarantine("automation_asset_preparation_incomplete");
        session.PublishCompleteDefinitions();
        return JournalRecoveryResult.Completed;
    }

    private static bool Stopped(AssetRecoveryInspection state) => state.IsWriterObservationComplete && state.Writers == WriterState.Stopped;
    private JournalRecoveryResult Quarantine(string reason) { session.Quarantine(reason); return JournalRecoveryResult.Quarantined; }
}
