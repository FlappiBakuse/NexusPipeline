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
        try
        {
            foreach (var (journalId, subject) in Pending())
            {
                var terminal = new AttemptTerminalStore(instance, database, contracts, lease);
                var state = terminal.Read(journalId);
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
                new RecoveryCoordinator(new RecoveryJournalRegistry(database), new SnapshotCompletionEvidence(instance, database, contracts), index).VerifyAndPublish();
                ConfigurationRecoveryHolds.Publish(database, index);
            }
            var records = new ExecutionRecordProjection(instance, database, contracts, lease, new(instance, database, contracts));
            Guid? cursor = null;
            do { cursor = records.ReconcileStoppedPage(cursor); } while (cursor is not null);
        }
        catch { index.BlockUnknown("execution_attempt_completion_reconciliation_required"); throw; }
    }

    private IReadOnlyList<(Guid JournalId, string Subject)> Pending()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT j.journal_id,o.subject_id FROM main.operation_journals j JOIN main.operations o ON o.operation_id=j.operation_id " +
            "WHERE j.operation_kind='runAttempt' AND j.phase IN ('prepared','applied') AND o.operation_kind='execution.start' LIMIT 10001;";
        using var rows = command.ExecuteReader(); var result = new List<(Guid, string)>();
        while (rows.Read())
        {
            if (result.Count == 10000) throw new IOException("execution_attempt_inventory_limit");
            result.Add((Guid.Parse(rows.GetString(0)), rows.GetString(1)));
        }
        return result;
    }
}
