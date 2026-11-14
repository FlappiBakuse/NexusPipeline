using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class RecoveryCapacityInventory
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        RecoveryCapacityStore.VerifyCounts(connection, transaction);
        foreach (var row in RecoveryCapacityStore.ReadReservations(connection, transaction))
        {
            var journal = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, row.Owner);
            RecoveryCapacityBudget expected;
            if (journal.Kind == JournalKind.RunAttempt)
            {
                var reference = new BlobReference(journal.PlanPath,
                    new FileInfo(Path.Combine(instance.RuntimeRoot, journal.PlanPath)).Length, journal.PlanDigest);
                var metadata = new AttemptPlanCodec(instance, contracts, new(instance, contracts)).ReadMetadata(reference);
                expected = ExecutionRecoveryCapacity.Measure(metadata.Total);
            }
            else
            {
                var effects = FileEffectRows.Read(connection, journal.JournalId, transaction);
                if (journal.Kind is JournalKind.UpdateApply or JournalKind.ExtensionApply)
                    effects = effects.Where(item => item.StepId is not ("restore-retire-candidate" or "restore-original")).ToArray();
                expected = RecoveryCapacityBudget.ForJournal(journal, effects);
            }
            if (row.Limit != expected) throw RecoveryCapacityStore.Invalid();
            if (journal.Kind == JournalKind.UpdateApply && row.Released)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM system.maintenance_operations WHERE operation_id=$operation AND state=2);";
                command.Parameters.AddWithValue("$operation", journal.OperationId.ToString("D"));
                var stopped = new JournalWriterStore(database).Observe(journal.JournalId);
                if (journal.Phase != JournalPhase.Committed || (long)command.ExecuteScalar()! != 1 ||
                    !stopped.IsComplete || stopped.State != NexusPipeline.Core.Execution.WriterState.Stopped) throw RecoveryCapacityStore.Invalid();
            }
        }
        transaction.Commit();
    }
}
