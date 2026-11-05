using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationRuntimeQueue(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal bool HasIndexedPending()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM notification_delivery_pending) OR " +
            "EXISTS(SELECT 1 FROM notification_receipt_progress INDEXED BY ix_notification_receipt_pending WHERE next_ordinal<route_count);";
        return (long)command.ExecuteScalar()! != 0;
    }

    internal bool ReadVerifiedPending()
    {
        var pending = false;
        NotificationReferenceInventory.Visit(instance, database, contracts,
            (_, expanded, _) => pending |= !expanded,
            (_, transition) => pending |= !NotificationDeliveryStates.ReleasesReferences(transition.State));
        return pending;
    }

    internal IReadOnlyList<(Guid Operation, BlobReference Reference)> Read(string state, Guid? after = null)
    {
        if (state is not ("queued" or "sending")) throw new ArgumentException("notification_pending_state_invalid");
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = new List<(Guid Operation, BlobReference Reference)>();
        using (var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT p.operation_id,d.relative_path,d.size_bytes,d.sha256 FROM notification_delivery_pending p " +
            "JOIN notification_deliveries d ON d.operation_id=p.operation_id WHERE p.state=$0 AND p.operation_id>$1 " +
            "ORDER BY p.operation_id LIMIT 4;", state, after?.ToString("D") ?? ""))
        using (var rows = command.ExecuteReader())
            while (rows.Read()) result.Add((Guid.ParseExact(rows.GetString(0), "D"), new(rows.GetString(1), rows.GetInt64(2), rows.GetString(3))));
        foreach (var item in result)
        {
            var saved = new NotificationDeliveryEvidence(instance, database, contracts).ReadFromSnapshot(item.Reference, connection, transaction);
            if (saved.Document.OperationId != item.Operation ||
                NotificationDeliveryRows.ReadState(connection, transaction, item.Operation).State.ToString().ToLowerInvariant() != state)
                throw new IOException("notification_pending_source_unbound");
        }
        transaction.Commit(); return result;
    }
}
