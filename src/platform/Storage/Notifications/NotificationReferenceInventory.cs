using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationReferenceInventory
{
    internal static void VerifyReadOnly(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System);
        using var transaction = DatabaseConnections.BeginRead(connection);
        foreach (var item in Pages(connection, transaction, "notification_receipt_sources"))
        {
            var source = new NotificationReceiptCodec(instance, database, contracts).ReadContext(item.Reference, connection, transaction);
            if (source.Snapshot.Document.OperationId != item.OperationId) throw NotificationReceiptCodec.Invalid();
            using var command = NotificationDeliveryRows.Command(connection, transaction,
                "SELECT ordinal,relative_path,size_bytes,sha256 FROM notifications.notification_receipt_routes WHERE operation_id=$0 ORDER BY ordinal;",
                item.OperationId.ToString("D"));
            using var row = command.ExecuteReader();
            var count = 0;
            while (row.Read())
            {
                var reference = new BlobReference(row.GetString(1), row.GetInt64(2), row.GetString(3));
                var saved = new ReceiptRouteCodec(instance, database, contracts).Read(reference, source, connection, transaction);
                if (saved.Ordinal != row.GetInt32(0)) throw NotificationReceiptCodec.Invalid();
                count++;
            }
            if (count > source.Routes.Length) throw NotificationReceiptCodec.Invalid();
        }
        foreach (var item in Pages(connection, transaction, "notification_saved_tests"))
        {
            var source = new TaskTestCodec(instance, database, contracts).Read(item.Reference, connection, transaction);
            if (source.Document.OperationId != item.OperationId) throw TaskTestCodec.Invalid();
        }
        foreach (var item in Pages(connection, transaction, "notification_scheduled_sources"))
        {
            var source = new NotificationScheduledCodec(instance, contracts).Read(item.Reference, connection, transaction);
            if (source.Document.OperationId != item.OperationId) throw NotificationScheduledCodec.Invalid();
        }
        foreach (var item in Pages(connection, transaction, "notification_manual_sources"))
        {
            var source = new NotificationManualCodec(instance, contracts).Read(item.Reference, connection, transaction);
            if (source.Document.OperationId != item.OperationId) throw NotificationManualCodec.Invalid();
        }
        foreach (var item in Pages(connection, transaction, "notification_deliveries"))
        {
            var saved = new NotificationDeliveryEvidence(instance, database, contracts).ReadFromSnapshot(item.Reference, connection, transaction);
            if (saved.Document.OperationId != item.OperationId) throw NotificationReceiptCodec.Invalid();
            _ = NotificationDeliveryRows.ReadState(connection, transaction, item.OperationId);
        }
        RequireNoOrphans(connection, transaction);
        transaction.Commit();
    }

    internal static IEnumerable<(Guid OperationId, BlobReference Reference)> Pages(SqliteConnection connection,
        SqliteTransaction transaction, string table)
    {
        var after = "";
        while (true)
        {
            var page = new List<(Guid OperationId, BlobReference Reference)>();
            using (var command = NotificationDeliveryRows.Command(connection, transaction,
                $"SELECT operation_id,relative_path,size_bytes,sha256 FROM {DatabaseTables.Name(table)} WHERE operation_id>$0 ORDER BY operation_id LIMIT 200;", after))
            using (var row = command.ExecuteReader())
                while (row.Read())
                {
                    var id = Guid.ParseExact(row.GetString(0), "D");
                    if (id == Guid.Empty || id.ToString("D") != row.GetString(0)) throw NotificationReceiptCodec.Invalid();
                    page.Add((id, new(row.GetString(1), row.GetInt64(2), row.GetString(3))));
                }
            if (page.Count == 0) yield break;
            foreach (var item in page) yield return item;
            after = page[^1].OperationId.ToString("D");
        }
    }

    private static readonly string FullOrphans = OrphanQuery(changedOnly: false);
    private static readonly string ChangedOrphans = OrphanQuery(changedOnly: true);

    internal static void RequireNoOrphans(SqliteConnection connection, SqliteTransaction transaction, bool changedOnly = false)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction, changedOnly ? ChangedOrphans : FullOrphans);
        if ((long)command.ExecuteScalar()! != 0) throw NotificationReceiptCodec.Invalid();
    }

    private static string OrphanQuery(bool changedOnly)
    {
        string Rows(string table, string alias, string? related = null) =>
            NotificationProjectionChanges.Rows(table, alias, changedOnly, relatedKey: related);
        var operations = Rows("main.operations", "o");
        return $"""
            SELECT EXISTS(SELECT 1 FROM {operations} LEFT JOIN notifications.notification_deliveries d ON d.operation_id=o.operation_id
                WHERE o.operation_kind='notification.delivery' AND d.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {operations} LEFT JOIN notifications.notification_receipt_sources s ON s.operation_id=o.operation_id
                WHERE o.operation_kind='notification.registration' AND s.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_delivery_secrets", "a")}
                LEFT JOIN notifications.notification_deliveries d ON d.operation_id=a.operation_id WHERE d.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_delivery_assets", "a")}
                LEFT JOIN notifications.notification_deliveries d ON d.operation_id=a.operation_id WHERE d.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_delivery_transitions", "a")}
                LEFT JOIN notifications.notification_deliveries d ON d.operation_id=a.operation_id WHERE d.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_receipt_progress", "p")}
                LEFT JOIN notifications.notification_receipt_sources s ON s.operation_id=p.operation_id WHERE s.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_receipt_sources", "s")}
                LEFT JOIN notifications.notification_receipt_progress p ON p.operation_id=s.operation_id WHERE p.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_delivery_pending", "a")}
                LEFT JOIN notifications.notification_deliveries d ON d.operation_id=a.operation_id WHERE d.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {operations} LEFT JOIN notifications.notification_saved_tests s ON s.operation_id=o.operation_id
                WHERE o.operation_kind='notification.saved-test' AND s.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_saved_tests", "s", "delivery_operation_id")}
                LEFT JOIN notifications.notification_deliveries d ON d.operation_id=s.delivery_operation_id
                WHERE d.operation_id IS NULL OR d.acceptance_id!=s.operation_id OR d.source_attempt_id IS NOT NULL)
            OR EXISTS(SELECT 1 FROM {operations} LEFT JOIN notifications.notification_scheduled_sources s ON s.operation_id=o.operation_id
                WHERE o.operation_kind='notification.scheduled-source' AND s.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {operations} LEFT JOIN notifications.notification_manual_sources s ON s.operation_id=o.operation_id
                WHERE o.operation_kind='notification.manual-source' AND s.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_manual_deliveries", "m", "delivery_operation_id")}
                LEFT JOIN notifications.notification_manual_sources s ON s.operation_id=m.operation_id
                LEFT JOIN notifications.notification_deliveries d ON d.operation_id=m.delivery_operation_id
                WHERE s.operation_id IS NULL OR d.operation_id IS NULL OR d.acceptance_id!=m.operation_id
                   OR d.task_id!=m.task_id OR d.source_attempt_id IS NOT NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_scheduled_deliveries", "m", "delivery_operation_id")}
                LEFT JOIN notifications.notification_scheduled_sources s ON s.operation_id=m.operation_id
                LEFT JOIN notifications.notification_deliveries d ON d.operation_id=m.delivery_operation_id
                WHERE s.operation_id IS NULL OR d.operation_id IS NULL OR d.acceptance_id!=m.operation_id
                   OR d.task_id!=m.task_id OR d.source_attempt_id IS NOT NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_receipt_secrets", "a")}
                LEFT JOIN notifications.notification_receipt_sources s ON s.operation_id=a.operation_id WHERE s.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_receipt_routes", "a")}
                LEFT JOIN notifications.notification_receipt_sources s ON s.operation_id=a.operation_id WHERE s.operation_id IS NULL)
            OR EXISTS(SELECT 1 FROM {Rows("notifications.notification_receipt_deliveries", "a", "delivery_operation_id")}
                LEFT JOIN notifications.notification_receipt_routes r ON r.operation_id=a.operation_id AND r.ordinal=a.route_ordinal
                LEFT JOIN notifications.notification_deliveries d ON d.operation_id=a.delivery_operation_id
                WHERE r.operation_id IS NULL OR d.operation_id IS NULL);
            """;
    }
}
