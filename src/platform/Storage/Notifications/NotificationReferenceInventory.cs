using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationReferenceInventory
{
    internal static void Visit(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        Action<ReceiptReadContext, bool, IReadOnlyList<BlobReference>> sourceVisit,
        Action<NotificationDeliverySnapshot, NotificationDeliveryTransition> deliveryVisit,
        Action<TaskTestSnapshot>? savedTestVisit = null,
        Action<NotificationWorkSnapshot>? scheduledVisit = null,
        Action<NotificationWorkSnapshot>? manualVisit = null)
    {
        var sources = new List<(ReceiptReadContext Source, bool Expanded, IReadOnlyList<BlobReference> Routes)>();
        var tests = new List<TaskTestSnapshot>();
        var scheduled = new List<NotificationWorkSnapshot>();
        var manual = new List<NotificationWorkSnapshot>();
        var deliveries = new List<(NotificationDeliverySnapshot Delivery, NotificationDeliveryTransition State)>();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System))
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            foreach (var item in Pages(connection, transaction, "notification_receipt_sources"))
            {
                var source = new NotificationReceiptCodec(instance, database, contracts).ReadContext(item.Reference, connection, transaction);
                if (source.Snapshot.Document.OperationId != item.OperationId) throw NotificationReceiptCodec.Invalid();
                var routes = new List<(int Ordinal, BlobReference Reference)>();
                using (var command = NotificationDeliveryRows.Command(connection, transaction,
                    "SELECT ordinal,relative_path,size_bytes,sha256 FROM notifications.notification_receipt_routes WHERE operation_id=$0 ORDER BY ordinal;",
                    item.OperationId.ToString("D")))
                using (var row = command.ExecuteReader())
                    while (row.Read()) routes.Add((row.GetInt32(0), new(row.GetString(1), row.GetInt64(2), row.GetString(3))));
                foreach (var route in routes)
                {
                    var saved = new ReceiptRouteCodec(instance, database, contracts).Read(route.Reference, source, connection, transaction);
                    if (saved.Ordinal != route.Ordinal) throw NotificationReceiptCodec.Invalid();
                }
                if (routes.Count > source.Routes.Length) throw NotificationReceiptCodec.Invalid();
                sources.Add((source, routes.Count == source.Routes.Length, routes.Select(item => item.Reference).ToArray()));
            }
            foreach (var item in Pages(connection, transaction, "notification_saved_tests"))
            {
                var source = new TaskTestCodec(instance, database, contracts).Read(item.Reference, connection, transaction);
                if (source.Document.OperationId != item.OperationId) throw TaskTestCodec.Invalid();
                if (savedTestVisit is not null) tests.Add(source);
            }
            foreach (var item in Pages(connection, transaction, "notification_scheduled_sources"))
            {
                var source = new NotificationScheduledCodec(instance, contracts).Read(item.Reference, connection, transaction);
                if (source.Document.OperationId != item.OperationId) throw NotificationScheduledCodec.Invalid();
                if (scheduledVisit is not null) scheduled.Add(source);
            }
            foreach (var item in Pages(connection, transaction, "notification_manual_sources"))
            {
                var source = new NotificationManualCodec(instance, contracts).Read(item.Reference, connection, transaction);
                if (source.Document.OperationId != item.OperationId) throw NotificationManualCodec.Invalid();
                if (manualVisit is not null) manual.Add(source);
            }
            foreach (var item in Pages(connection, transaction, "notification_deliveries"))
            {
                var saved = new NotificationDeliveryEvidence(instance, database, contracts).ReadFromSnapshot(item.Reference, connection, transaction);
                if (saved.Document.OperationId != item.OperationId) throw NotificationReceiptCodec.Invalid();
                deliveries.Add((saved, NotificationDeliveryRows.ReadState(connection, transaction, item.OperationId)));
            }
            RequireNoOrphans(connection, transaction);
            transaction.Commit();
        }
        foreach (var item in sources) sourceVisit(item.Source, item.Expanded, item.Routes);
        foreach (var item in tests) savedTestVisit!(item);
        foreach (var item in scheduled) scheduledVisit!(item);
        foreach (var item in manual) manualVisit!(item);
        foreach (var item in deliveries) deliveryVisit(item.Delivery, item.State);
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

    internal static void RequireNoOrphans(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM main.operations o LEFT JOIN notifications.notification_deliveries d ON d.operation_id=o.operation_id " +
            "WHERE o.operation_kind='notification.delivery' AND d.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM main.operations o LEFT JOIN notifications.notification_receipt_sources s ON s.operation_id=o.operation_id " +
            "WHERE o.operation_kind='notification.registration' AND s.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_delivery_secrets a LEFT JOIN notifications.notification_deliveries d ON d.operation_id=a.operation_id WHERE d.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_delivery_assets a LEFT JOIN notifications.notification_deliveries d ON d.operation_id=a.operation_id WHERE d.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_delivery_transitions a LEFT JOIN notifications.notification_deliveries d ON d.operation_id=a.operation_id WHERE d.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_receipt_progress p LEFT JOIN notifications.notification_receipt_sources s ON s.operation_id=p.operation_id WHERE s.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_receipt_sources s LEFT JOIN notifications.notification_receipt_progress p ON p.operation_id=s.operation_id WHERE p.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_delivery_pending a LEFT JOIN notifications.notification_deliveries d ON d.operation_id=a.operation_id WHERE d.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM main.operations o LEFT JOIN notifications.notification_saved_tests s ON s.operation_id=o.operation_id WHERE o.operation_kind='notification.saved-test' AND s.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_saved_tests s LEFT JOIN notifications.notification_deliveries d ON d.operation_id=s.delivery_operation_id WHERE d.operation_id IS NULL OR d.acceptance_id!=s.operation_id OR d.source_attempt_id IS NOT NULL) " +
            "OR EXISTS(SELECT 1 FROM main.operations o LEFT JOIN notifications.notification_scheduled_sources s ON s.operation_id=o.operation_id WHERE o.operation_kind='notification.scheduled-source' AND s.operation_id IS NULL) " +
            ("OR EXISTS(SELECT 1 FROM main.operations o LEFT JOIN notifications.notification_manual_sources s ON s.operation_id=o.operation_id WHERE o.operation_kind='notification.manual-source' AND s.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_manual_deliveries m LEFT JOIN notifications.notification_manual_sources s ON s.operation_id=m.operation_id LEFT JOIN notifications.notification_deliveries d ON d.operation_id=m.delivery_operation_id WHERE s.operation_id IS NULL OR d.operation_id IS NULL OR d.acceptance_id!=m.operation_id OR d.task_id!=m.task_id OR d.source_attempt_id IS NOT NULL) ") +
            "OR EXISTS(SELECT 1 FROM notifications.notification_scheduled_deliveries m LEFT JOIN notifications.notification_scheduled_sources s ON s.operation_id=m.operation_id LEFT JOIN notifications.notification_deliveries d ON d.operation_id=m.delivery_operation_id WHERE s.operation_id IS NULL OR d.operation_id IS NULL OR d.acceptance_id!=m.operation_id OR d.task_id!=m.task_id OR d.source_attempt_id IS NOT NULL) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_receipt_secrets a LEFT JOIN notifications.notification_receipt_sources s ON s.operation_id=a.operation_id WHERE s.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_receipt_routes a LEFT JOIN notifications.notification_receipt_sources s ON s.operation_id=a.operation_id WHERE s.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_receipt_deliveries a LEFT JOIN notifications.notification_receipt_routes r ON r.operation_id=a.operation_id AND r.ordinal=a.route_ordinal " +
            "LEFT JOIN notifications.notification_deliveries d ON d.operation_id=a.delivery_operation_id WHERE r.operation_id IS NULL OR d.operation_id IS NULL);");
        if ((long)command.ExecuteScalar()! != 0) throw NotificationReceiptCodec.Invalid();
    }
}
