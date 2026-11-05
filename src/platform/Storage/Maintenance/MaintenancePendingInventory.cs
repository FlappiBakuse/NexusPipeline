using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal static class MaintenancePendingInventory
{
    internal static void VerifyReadOnly(RuntimeInstance instance, StateDatabase database, ContractReader contracts, PayloadApplyRoots roots) =>
        Visit(instance, database, contracts, roots, verifyContents: true, (_, _) => false);

    internal static bool IsReferenced(RuntimeInstance instance, StateDatabase database, ContractReader contracts, PayloadApplyRoots? roots, BlobReference blob)
        => ReadReferenced(instance, database, contracts, roots, new HashSet<BlobReference> { blob }).Contains(blob);

    internal static HashSet<BlobReference> ReadReferenced(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        PayloadApplyRoots? roots, IReadOnlySet<BlobReference> candidates)
    {
        var referenced = new HashSet<BlobReference>();
        if (database.InstanceId != instance.InstanceId || !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot))
            throw new IOException("maintenance_inventory_instance_unbound");
        if (roots is null)
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            using var command = connection.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM maintenance_operations) " +
                "OR EXISTS(SELECT 1 FROM operations WHERE operation_kind='maintenance.update');";
            if ((long)command.ExecuteScalar()! != 0) throw new IOException("blob_reference_maintenance_roots_unavailable");
            return referenced;
        }
        Visit(instance, database, contracts, roots, verifyContents: false, (plan, source) =>
        {
            referenced.UnionWith(plan.Before.Contents.Concat(plan.Candidate.Contents)
                .Concat(UpdateSourceCodec.References(source)).Where(candidates.Contains));
            return false;
        });
        return referenced;
    }

    private static bool Visit(RuntimeInstance instance, StateDatabase database, ContractReader contracts, PayloadApplyRoots roots,
        bool verifyContents, Func<FrozenPayloadApply, HostUpdateSource, bool> visit)
    {
        if (database.InstanceId != instance.InstanceId || !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot))
            throw new IOException("maintenance_inventory_instance_unbound");
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var rows = MaintenanceOperationRows.ReadAll(connection, transaction);
        using (var missing = connection.CreateCommand())
        {
            missing.Transaction = transaction;
            missing.CommandText = "SELECT COUNT(*) FROM operations o LEFT JOIN maintenance_operations m ON m.operation_id=o.operation_id " +
                "WHERE o.operation_kind='maintenance.update' AND m.operation_id IS NULL;";
            if ((long)missing.ExecuteScalar()! != 0) throw new IOException("maintenance_operation_metadata_missing");
        }
        var sourceCodec = new UpdateSourceCodec(instance, contracts);
        var plans = new PayloadPlanCodec(instance, contracts, new(instance, contracts), roots);
        var referenced = false;
        foreach (var row in rows)
        {
            var source = sourceCodec.ReadMetadata(row.Source);
            sourceCodec.RequireAcceptedInside(connection, transaction, source);
            var plan = verifyContents ? plans.Read(row.Plan) : plans.ReadStructure(row.Plan);
            MaintenancePendingStore.RequireRow(row, source, plan);
            if (row.InitialJournalId is null)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT COUNT(*) FROM operation_journals WHERE operation_id=$operation;";
                command.Parameters.AddWithValue("$operation", row.Identity.OperationId.ToString("D"));
                if ((long)command.ExecuteScalar()! != 0) throw new IOException("maintenance_pending_has_journal");
            }
            else
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT operation_id,operation_kind,plan_path,plan_digest,phase FROM operation_journals WHERE journal_id=$journal;";
                command.Parameters.AddWithValue("$journal", row.InitialJournalId.Value.ToString("D"));
                using var journal = command.ExecuteReader();
                if (!journal.Read() || journal.GetString(0) != row.Identity.OperationId.ToString("D") || journal.GetString(1) != "updateApply" ||
                    journal.GetString(2) != row.Plan.RelativePath || journal.GetString(3) != row.Plan.Sha256 ||
                    row.State == HostUpdateState.Completed && journal.GetString(4) != "committed" ||
                    row.State == HostUpdateState.Quarantined && journal.GetString(4) != "quarantined" ||
                    row.State == HostUpdateState.Starting && (journal.GetString(4) is "committed" or "quarantined"))
                    throw new IOException("maintenance_journal_metadata_unbound");
            }
            referenced |= visit(plan, source);
        }
        transaction.Commit(); return referenced;
    }
}
