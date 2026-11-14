using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationReferenceMetadata
{
    internal static void Visit(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        Action<NotificationReceiptSnapshot, bool, IReadOnlyList<BlobReference>> sourceVisit,
        Action<NotificationDeliverySnapshot, NotificationDeliveryTransition> deliveryVisit,
        Action<TaskTestSnapshot> savedTestVisit, Action<NotificationWorkSnapshot> scheduledVisit,
        Action<NotificationWorkSnapshot> manualVisit)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var item in NotificationReferenceInventory.Pages(connection, transaction, "notification_receipt_sources"))
        {
            var source = new NotificationReceiptCodec(instance, database, contracts).ReadMetadata(item.Reference, connection, transaction);
            if (source.Document.OperationId != item.OperationId) throw NotificationReceiptCodec.Invalid();
            var next = ReceiptProgressRows.Require(connection, transaction, item.OperationId, source.Document.RouteCount);
            using var command = NotificationDeliveryRows.Command(connection, transaction,
                "SELECT ordinal,relative_path,size_bytes,sha256 FROM notifications.notification_receipt_routes WHERE operation_id=$0 ORDER BY ordinal;",
                item.OperationId.ToString("D"));
            var routes = new List<BlobReference>();
            using (var rows = command.ExecuteReader())
                while (rows.Read())
                {
                    var reference = new BlobReference(rows.GetString(1), rows.GetInt64(2), rows.GetString(3));
                    var route = new NotificationCatalogCodec(instance, contracts).ReadDocument<ReceiptRouteDocument>(reference);
                    if (route.Format != "nexus.notification.receipt-route" || route.Version != 1 ||
                        route.SourceOperationId != item.OperationId || route.Ordinal != rows.GetInt32(0)) throw NotificationReceiptCodec.Invalid();
                    routes.Add(reference);
                }
            sourceVisit(source, next == source.Document.RouteCount, routes);
        }
        foreach (var item in NotificationReferenceInventory.Pages(connection, transaction, "notification_saved_tests"))
        {
            var source = new TaskTestCodec(instance, database, contracts).Read(item.Reference, connection, transaction);
            if (source.Document.OperationId != item.OperationId) throw TaskTestCodec.Invalid();
            savedTestVisit(source);
        }
        foreach (var item in NotificationReferenceInventory.Pages(connection, transaction, "notification_scheduled_sources"))
        {
            var source = new NotificationScheduledCodec(instance, contracts).Read(item.Reference, connection, transaction);
            if (source.Document.OperationId != item.OperationId) throw NotificationScheduledCodec.Invalid();
            scheduledVisit(source);
        }
        foreach (var item in NotificationReferenceInventory.Pages(connection, transaction, "notification_manual_sources"))
        {
            var source = new NotificationManualCodec(instance, contracts).Read(item.Reference, connection, transaction);
            if (source.Document.OperationId != item.OperationId) throw NotificationManualCodec.Invalid();
            manualVisit(source);
        }
        foreach (var item in NotificationReferenceInventory.Pages(connection, transaction, "notification_deliveries"))
        {
            var delivery = new NotificationDeliveryEvidence(instance, database, contracts).ReadMetadata(item.Reference, connection, transaction);
            if (delivery.Document.OperationId != item.OperationId) throw NotificationReceiptCodec.Invalid();
            deliveryVisit(delivery, NotificationDeliveryRows.ReadState(connection, transaction, item.OperationId));
        }
        NotificationReferenceInventory.RequireNoOrphans(connection, transaction);
        transaction.Commit();
    }
}
