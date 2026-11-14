using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationWorkFanout
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database, ContractReader contracts, NotificationWorkSnapshot source,
        SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        if (table is not ("notification_scheduled_deliveries" or "notification_manual_deliveries")) throw NotificationWorkShape.Invalid();
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            $"SELECT d.relative_path,d.size_bytes,d.sha256 FROM {DatabaseTables.Name(table)} m JOIN notifications.notification_deliveries d ON d.operation_id=m.delivery_operation_id WHERE m.operation_id=$0 ORDER BY m.task_id;",
            source.Document.OperationId.ToString("D"));
        var deliveries = new List<BlobReference>();
        using (var rows = command.ExecuteReader()) while (rows.Read()) deliveries.Add(new(rows.GetString(0), rows.GetInt64(1), rows.GetString(2)));
        if (deliveries.Count != source.Document.Targets.Count(item => item.Disposition == "queued")) throw NotificationWorkShape.Invalid();
        foreach (var delivery in deliveries)
        {
            var saved = new NotificationDeliveryEvidence(instance, database, contracts).ReadFromSnapshot(delivery, connection, transaction);
            var state = NotificationDeliveryRows.ReadState(connection, transaction, saved.Document.OperationId);
            NotificationDeliveryRows.RequirePendingProjection(connection, transaction, state, source.Document.OperationId);
        }
    }
}
