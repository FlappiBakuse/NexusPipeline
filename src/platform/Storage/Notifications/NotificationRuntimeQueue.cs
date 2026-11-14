using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationRuntimeQueue(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal bool HasIndexedPending()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM notifications.notification_delivery_pending) OR " +
            "EXISTS(SELECT 1 FROM notifications.notification_receipt_progress INDEXED BY ix_notification_receipt_pending WHERE next_ordinal<route_count);";
        return (long)command.ExecuteScalar()! != 0;
    }

    internal void VerifyProjection()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications);
        using var transaction = connection.BeginTransaction(deferred: true);
        NotificationReferenceInventory.RequireNoOrphans(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM notifications.notification_deliveries d " +
            "LEFT JOIN notifications.notification_delivery_transitions t ON t.operation_id=d.operation_id AND " +
            "t.ordinal=(SELECT max(ordinal) FROM notifications.notification_delivery_transitions WHERE operation_id=d.operation_id) " +
            "LEFT JOIN notifications.notification_delivery_pending p ON p.operation_id=d.operation_id WHERE t.operation_id IS NULL OR " +
            "json_type(t.document_json,'$.state') IS NOT 'text' OR " +
            "json_extract(t.document_json,'$.state') NOT IN ('queued','sending','unknown','succeeded','failed','skipped') OR " +
            "(json_extract(t.document_json,'$.state') IN ('queued','sending','unknown') AND " +
            "(p.operation_id IS NULL OR p.state<>json_extract(t.document_json,'$.state'))) OR " +
            "(json_extract(t.document_json,'$.state') IN ('succeeded','failed','skipped') AND p.operation_id IS NOT NULL)) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_receipt_progress p WHERE p.next_ordinal<0 OR p.next_ordinal>p.route_count " +
            "OR p.next_ordinal<>(SELECT count(*) FROM notifications.notification_receipt_routes r WHERE r.operation_id=p.operation_id) " +
            "OR (p.next_ordinal>0 AND ((SELECT min(ordinal) FROM notifications.notification_receipt_routes r WHERE r.operation_id=p.operation_id)<>0 " +
            "OR (SELECT max(ordinal) FROM notifications.notification_receipt_routes r WHERE r.operation_id=p.operation_id)<>p.next_ordinal-1)));";
        if ((long)command.ExecuteScalar()! != 0) throw NotificationReceiptCodec.Invalid();
        transaction.Commit();
    }

    internal IReadOnlyList<(Guid Operation, BlobReference Reference)> Read(string state, Guid? after = null,
        NotificationAttemptReads? reads = null)
    {
        if (state is not ("queued" or "sending")) throw new ArgumentException("notification_pending_state_invalid");
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = new List<(Guid Operation, BlobReference Reference)>();
        using (var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT p.operation_id,d.relative_path,d.size_bytes,d.sha256 FROM notifications.notification_delivery_pending p " +
            "JOIN notifications.notification_deliveries d ON d.operation_id=p.operation_id WHERE p.state=$0 AND p.operation_id>$1 " +
            "ORDER BY p.operation_id LIMIT 4;", state, after?.ToString("D") ?? ""))
        using (var rows = command.ExecuteReader())
            while (rows.Read()) result.Add((Guid.ParseExact(rows.GetString(0), "D"), new(rows.GetString(1), rows.GetInt64(2), rows.GetString(3))));
        foreach (var item in result)
        {
            reads?.HoldDelivery(item.Operation);
            var saved = new NotificationDeliveryEvidence(instance, database, contracts).ReadFromSnapshot(item.Reference, connection, transaction);
            if (saved.Document.OperationId != item.Operation ||
                NotificationDeliveryRows.ReadState(connection, transaction, item.Operation).State.ToString().ToLowerInvariant() != state)
                throw new IOException("notification_pending_source_unbound");
        }
        transaction.Commit(); return result;
    }
}
