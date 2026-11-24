using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationWorkFanout
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database, ContractReader contracts, NotificationWorkSnapshot source,
        SqliteConnection connection, SqliteTransaction transaction)
    {
        var (table, subject, kind) = source.Document.Format switch
        {
            NotificationManualCodec.Format => ("notification_manual_deliveries", NotificationManualCodec.Subject, "manualInvocation"),
            NotificationScheduledCodec.Format => ("notification_scheduled_deliveries", NotificationScheduledCodec.Subject, "scheduledInvocation"),
            _ => throw NotificationWorkShape.Invalid()
        };
        var targets = source.Document.Targets.Where(item => item.Disposition == "queued")
            .ToDictionary(item => item.TaskId.ToString("D"), StringComparer.Ordinal);
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            $"SELECT m.task_id,m.delivery_operation_id,d.relative_path,d.size_bytes,d.sha256 FROM {DatabaseTables.Name(table)} m JOIN notifications.notification_deliveries d ON d.operation_id=m.delivery_operation_id WHERE m.operation_id=$0 ORDER BY m.task_id;",
            source.Document.OperationId.ToString("D"));
        var deliveries = new List<(string TaskId, string OperationId, BlobReference Reference)>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
                deliveries.Add((rows.GetString(0), rows.GetString(1), new(rows.GetString(2), rows.GetInt64(3), rows.GetString(4))));
        if (deliveries.Count != targets.Count) throw NotificationWorkShape.Invalid();
        var evidence = new NotificationDeliveryEvidence(instance, database, contracts);
        foreach (var delivery in deliveries)
        {
            if (!targets.Remove(delivery.TaskId, out var target)) throw NotificationWorkShape.Invalid();
            var saved = evidence.ReadMetadata(delivery.Reference, connection, transaction);
            var expected = NotificationWorkShape.Delivery(instance.InstanceId, source, target, subject, kind);
            if (saved.Document.OperationId.ToString("D") != delivery.OperationId ||
                !NotificationCatalogCodec.Encode(saved.Document).AsSpan().SequenceEqual(NotificationCatalogCodec.Encode(expected)))
                throw NotificationWorkShape.Invalid();
        }
    }
}
