using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed class PayloadPreparedProjection(JournalStore store, RecoveryJournalRegistry registry, FileEffectOutbox outbox,
    IPayloadPersistenceObserver? observer)
{
    internal void Synchronize(FrozenPayloadApply plan, bool verifiedNoEffects)
    {
        var registered = registry.ReadJournal(plan.JournalId);
        foreach (var next in store.ReadAllRevisions(plan.JournalId).Where(item => item.Revision > registered.Revision))
        {
            var recorded = PayloadPreparedBinding.HasUnexecutedReceipt(next.Root);
            if (next.Phase == JournalPhase.Committed && (!recorded || !verifiedNoEffects))
                throw new IOException("payload_recorded_commit_unverified");
            if (recorded && verifiedNoEffects)
            {
                var digest = Convert.ToHexStringLower(SHA256.HashData(next.Export()));
                foreach (var effect in outbox.Read(plan.JournalId).Where(item => item.State == FileEffectState.Pending))
                {
                    outbox.Record(plan.JournalId, effect.StepId, effect.State, FileEffectState.NotExecuted, digest, new(true, false, false, false));
                    observer?.Reached(PayloadPersistencePoint.OutboxCommitted, plan.JournalId, next.Revision, next.Phase);
                }
            }
            registry.Database.Write((connection, transaction) =>
            {
                registry.AdvanceInside(connection, transaction,
                    registered with { Revision = next.Revision, Phase = next.Phase }, next.Revision - 1,
                    recorded && verifiedNoEffects ? new(true, false, false, false) : null);
                if (plan.Kind == JournalKind.UpdateApply && (next.Phase is JournalPhase.Committed or JournalPhase.Quarantined))
                    MaintenanceOperationOutcome.ProjectNoEffectInside(connection, transaction, plan, registered.PlanDigest, next.Phase,
                        next.Root.GetProperty("receipts").EnumerateArray().Last().GetProperty("detailCode").GetString()!);
                if (plan.Kind == JournalKind.ExtensionApply && next.Phase == JournalPhase.Committed)
                {
                    if (plan.ExtensionSource is null) throw new IOException("extension_no_effect_source_unbound");
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation " +
                        "AND operation_kind='extension.apply' AND status='accepted' AND initial_journal_id=$journal;";
                    command.Parameters.AddWithValue("$operation", plan.OperationId.ToString("D"));
                    command.Parameters.AddWithValue("$journal", plan.JournalId.ToString("D"));
                    if (command.ExecuteNonQuery() != 1) throw new IOException("extension_no_effect_operation_unbound");
                }
                return true;
            });
            observer?.Reached(PayloadPersistencePoint.SqlProjectionCommitted, plan.JournalId, next.Revision, next.Phase);
        }
    }
}
