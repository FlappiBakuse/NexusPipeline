using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Core.Execution;

public sealed record EnteredAttemptInspection(JournalPhase Phase, bool DatabaseAgrees, EffectGateState Gate,
    bool IsWriterObservationComplete, WriterState Writers, bool AreWorkersCollected, bool IsConfigurationRestored,
    bool CanReconcileRecordedCommit, AttemptFacts Facts)
{
    public bool CanDeferConfigurationCompletion { get; init; }
}

public interface IEnteredConfigurationCompletion
{
    void VerifyPendingCompletion();
}

public interface IEnteredRecoverySession
{
    EnteredAttemptInspection Inspect();
    void FreezeDispatch();
    void StopWriters();
    void CollectWorkers();
    void MarkApplied();
    void RestoreConfiguration();
    void CommitInterrupted(AttemptFacts facts);
    void Quarantine(AttemptFacts facts);
    void ReconcileRecordedCommit();
}

public sealed class EnteredAttemptRecovery(IEnteredRecoverySession session)
{
    public JournalRecoveryResult Recover(bool isExplicitQuarantineRecovery = false)
    {
        var initial = session.Inspect(); Validate(initial);
        if (initial.Phase == JournalPhase.Committed)
        {
            if (!initial.DatabaseAgrees)
            {
                if (!Stopped(initial) || !initial.AreWorkersCollected || !initial.IsConfigurationRestored || !initial.CanReconcileRecordedCommit)
                    throw new InvalidOperationException("attempt_recorded_commit_unverified");
                session.ReconcileRecordedCommit();
                var reconciled = Read(initial);
                if (reconciled.Phase != JournalPhase.Committed || !reconciled.DatabaseAgrees || reconciled.Facts != initial.Facts)
                    throw new InvalidOperationException("attempt_recorded_commit_unverified");
            }
            return JournalRecoveryResult.AlreadyCompleted;
        }
        if (initial.Phase == JournalPhase.Quarantined && !isExplicitQuarantineRecovery) return JournalRecoveryResult.Quarantined;
        if (initial.Facts.Lifecycle != LifecycleOutcome.Running) throw new InvalidOperationException("attempt_active_facts_unbound");
        session.FreezeDispatch();
        var current = Read(initial);
        if (!Stopped(current)) { session.StopWriters(); current = Read(current); }
        if (!Stopped(current)) return Quarantine(current, "attempt_writer_stop_unconfirmed");
        if (!current.AreWorkersCollected) { session.CollectWorkers(); current = Read(current); }
        if (!Stopped(current) || !current.AreWorkersCollected) return Quarantine(current, "attempt_workers_uncollected");
        if (current.Phase == JournalPhase.Quarantined && !current.IsConfigurationRestored)
        {
            session.RestoreConfiguration(); current = Read(current);
            if (!Stopped(current) || !current.AreWorkersCollected || !current.IsConfigurationRestored)
                return Quarantine(current, "attempt_configuration_not_restored");
        }
        if (current.Phase != JournalPhase.Applied || !current.DatabaseAgrees)
        {
            session.MarkApplied(); current = Read(current);
            if (current.Phase != JournalPhase.Applied || !current.DatabaseAgrees || !Stopped(current) || !current.AreWorkersCollected)
                throw new InvalidOperationException("attempt_applied_facts_unverified");
        }
        if (!current.IsConfigurationRestored && current.CanDeferConfigurationCompletion && session is IEnteredConfigurationCompletion pending)
        {
            pending.VerifyPendingCompletion(); current = Read(current);
            if (current.Phase != JournalPhase.Applied || !current.DatabaseAgrees || !Stopped(current) ||
                !current.AreWorkersCollected || !current.CanDeferConfigurationCompletion)
                throw new InvalidOperationException("attempt_pending_configuration_completion_unverified");
            return JournalRecoveryResult.PendingConfigurationCompletion;
        }
        if (!current.IsConfigurationRestored) { session.RestoreConfiguration(); current = Read(current); }
        if (!Stopped(current) || !current.AreWorkersCollected || !current.IsConfigurationRestored)
            return Quarantine(current, "attempt_configuration_not_restored");
        var facts = current.Facts.AfterInterruption(WriterState.Stopped, true);
        session.CommitInterrupted(facts);
        var committed = Read(current);
        if (committed.Phase != JournalPhase.Committed || !committed.DatabaseAgrees || committed.Facts != facts ||
            !Stopped(committed) || !committed.AreWorkersCollected || !committed.IsConfigurationRestored)
            throw new InvalidOperationException("attempt_interrupted_commit_unverified");
        return JournalRecoveryResult.Restored;
    }

    private EnteredAttemptInspection Read(EnteredAttemptInspection previous)
    {
        var current = session.Inspect(); Validate(current);
        if (current.Facts.RunId != previous.Facts.RunId || current.Facts.AttemptId != previous.Facts.AttemptId ||
            current.Facts.Ordinal != previous.Facts.Ordinal ||
            previous.Facts.Engine is not (EngineOutcome.Running or EngineOutcome.Unknown) && current.Facts.Engine != previous.Facts.Engine ||
            previous.Facts.Business != BusinessOutcome.Unverified && current.Facts.Business != previous.Facts.Business)
            throw new InvalidOperationException("attempt_recovery_known_facts_changed");
        return current;
    }

    private static void Validate(EnteredAttemptInspection value)
    {
        if (value.Gate != EffectGateState.Entered || value.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or
            JournalPhase.Applied or JournalPhase.Committed or JournalPhase.Quarantined) || value.Facts is null ||
            value.Facts.RunId == Guid.Empty || value.Facts.AttemptId == Guid.Empty || value.Facts.Ordinal < 1 ||
            !Enum.IsDefined(value.Facts.Engine) || !Enum.IsDefined(value.Facts.Business) || !Enum.IsDefined(value.Facts.Lifecycle) ||
            !Enum.IsDefined(value.Facts.Recovery) || !Enum.IsDefined(value.Writers))
            throw new InvalidOperationException("attempt_entered_recovery_unrecognized");
    }

    private JournalRecoveryResult Quarantine(EnteredAttemptInspection current, string reason)
    {
        session.Quarantine(current.Facts with { Recovery = RecoveryOutcome.Quarantined, ReasonCode = reason });
        return JournalRecoveryResult.Quarantined;
    }

    private static bool Stopped(EnteredAttemptInspection value) => value.IsWriterObservationComplete && value.Writers == WriterState.Stopped;
}
