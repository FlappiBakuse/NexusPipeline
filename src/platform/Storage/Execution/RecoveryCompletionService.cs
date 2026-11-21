using System.Runtime.Versioning;
using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
public sealed class RecoveryCompletionService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ResourceLeaseManager resources, RecoveryIndex index)
{
    public void ReconcilePending()
    {
        lease.VerifyHeld(instance);
        var reads = ReferenceEquals(lease.Database, database) ? lease.BindAttemptReads(database) : null;
        try
        {
            foreach (var (journalId, subject) in Pending())
            {
                using var history = new HistoryReadScope(instance, database, contracts, observeExternalCommits: true);
                using var attempt = reads?.HoldTerminal(instance, database, contracts, journalId, requireFreshInventory: true);
                using var parents = attempt is null ? null : JournalReadScope.HoldParents(instance, contracts, reads);
                if (attempt is not null) JournalReadScope.SelectParent(instance, contracts, journalId);
                var terminal = new AttemptTerminalStore(instance, database, contracts, lease);
                var state = terminal.Read(journalId);
                var scoped = index.Current.Isolations.Any(item => item.JournalId == journalId && item.CanOrdinaryExit);
                if (state.Latest.Phase == JournalPhase.Quarantined)
                {
                    if (!scoped || !TryPrepareRestoredAttempt(terminal, state)) continue;
                    PublishRecovery();
                    state = terminal.Read(journalId);
                }
                else if (scoped && !TryPrepareRestoredAttempt(terminal, state)) continue;
                if (state.Gate.State == EffectGateState.Sealed && state.Cancellation?.ReasonCode is not
                    (DispatchIntentCodec.Reason or DispatchIntentCodec.InterruptionReason or DispatchIntentCodec.BudgetReason or DispatchIntentCodec.TargetReason)) continue;
                if (state.Gate.State == EffectGateState.Unentered) continue;
                var groups = state.Attempt.ConfigurationPlans.Count == 0 ? ImmutableArray<ConfigurationRunStatus>.Empty :
                    ConfigurationCompletionBinding.Read(instance, database, contracts, journalId);
                foreach (var group in groups.Where(group => group.CompletionOperationId is null))
                {
                    var current = terminal.Read(journalId);
                    var prior = SnapshotIntentCodec.Read(current.Latest.Root).SingleOrDefault(item => item.SessionId == group.SessionId);
                    new RunCompletionService(instance, database, contracts, lease, resources, index)
                        .CompleteInterrupted(prior?.CompletionOperationId ?? Guid.NewGuid(), subject, group.SessionId);
                }
                state = terminal.Read(journalId);
                if (state.Gate.State == EffectGateState.Sealed)
                {
                    if (state.Cancellation!.ReasonCode == DispatchIntentCodec.Reason) _ = terminal.CommitPreparedCancellation(journalId);
                    else _ = terminal.CommitPreparedStop(journalId, state.Cancellation.ReasonCode == DispatchIntentCodec.BudgetReason
                        ? LifecycleOutcome.Blocked : LifecycleOutcome.Interrupted, state.Cancellation.ReasonCode);
                }
                else _ = new EnteredAttemptRecovery(new EnteredRecoverySession(instance, database, contracts, lease, journalId, () =>
                {
                    var actual = new JournalWriterStore(database).ObserveForCurrentExecutor(journalId);
                    return new(instance.InstanceId, journalId, actual.IsComplete, actual.State);
                })).Recover();
                var committed = new AttemptReceiptStore(database).Read(Guid.Parse(state.Attempt.AttemptId))
                    ?? throw new IOException("record_recovery_receipt_missing");
                new ExecutionRecordProjection(instance, database, contracts, lease, new(instance, database, contracts)).ReconcileStopped(committed);
                PublishRecovery();
            }
            var records = new ExecutionRecordProjection(instance, database, contracts, lease, new(instance, database, contracts));
            Guid? cursor = null;
            do { cursor = records.ReconcileStoppedPage(cursor); } while (cursor is not null);
        }
        catch { index.BlockUnknown("execution_attempt_completion_reconciliation_required"); throw; }
    }

    private bool TryPrepareRestoredAttempt(AttemptTerminalStore terminal, AttemptWriterState state)
    {
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true,
                   domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        {
            var restored = AttemptTerminalEvidence.IsRestoredInside(connection, transaction, state, requireCompletedGroups: false);
            transaction.Commit();
            if (!restored) return false;
        }
        IDisposable scene;
        try
        {
            scene = RunBindingEvidence.HoldRestored(instance, database, contracts, state, state.Total.Steps.Select(step => step.StepId));
        }
        catch (Exception error) when (ScopedConfigurationRecovery.CanDeferScene(error))
        { return false; }
        using (scene)
            if (state.Latest.Phase == JournalPhase.Quarantined) terminal.MarkApplied(state.Journal.JournalId);
        return true;
    }

    private void PublishRecovery()
    {
        new RecoveryCoordinator(new RecoveryJournalRegistry(database), new SnapshotCompletionEvidence(instance, database, contracts), index)
            .VerifyAndPublish();
        ConfigurationRecoveryHolds.Publish(database, index);
    }

    private IReadOnlyList<(Guid JournalId, string Subject)> Pending()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT j.journal_id,o.subject_id FROM main.operation_journals j JOIN main.operations o ON o.operation_id=j.operation_id " +
            "WHERE j.operation_kind='runAttempt' AND j.phase IN ('prepared','applied','quarantined') AND o.operation_kind='execution.start' LIMIT 10001;";
        using var rows = command.ExecuteReader(); var result = new List<(Guid, string)>();
        while (rows.Read())
        {
            if (result.Count == 10000) throw new IOException("execution_attempt_inventory_limit");
            result.Add((Guid.Parse(rows.GetString(0)), rows.GetString(1)));
        }
        return result;
    }
}
