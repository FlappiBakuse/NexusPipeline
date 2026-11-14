using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class NotificationManualRows
{
    internal static BlobReference? Lookup(SqliteConnection connection, SqliteTransaction transaction, Guid operation)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM notifications.notification_manual_sources WHERE operation_id=$0;", operation.ToString("D"));
        using var row = command.ExecuteReader(); return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : null;
    }

    internal static void InsertInside(SqliteConnection connection, SqliteTransaction transaction, NotificationWorkSnapshot source, Guid invocation, DateTimeOffset created)
    {
        if (!OperationRows.Accept(connection, transaction, NotificationManualCodec.Identity(source), created).IsNew) throw NotificationManualCodec.Invalid();
        using var insert = NotificationDeliveryRows.Command(connection, transaction,
            "INSERT INTO notifications.notification_manual_sources VALUES($0,$1,$2,$3,$4);", source.Document.OperationId.ToString("D"), invocation.ToString("D"),
            source.Reference.RelativePath, source.Reference.SizeBytes, source.Reference.Sha256); insert.ExecuteNonQuery();
        foreach (var target in source.Document.Targets.Where(item => item.Disposition == "queued"))
        {
            using var member = NotificationDeliveryRows.Command(connection, transaction,
                "INSERT INTO notifications.notification_manual_deliveries VALUES($0,$1,$2);", source.Document.OperationId.ToString("D"), target.TaskId.ToString("D"),
                NotificationScheduledCodec.DeliveryId(source.Document.OperationId, target.TaskId).ToString("D")); member.ExecuteNonQuery();
        }
        using var complete = NotificationDeliveryRows.Command(connection, transaction,
            "UPDATE main.operations SET status='completed' WHERE operation_id=$0 AND status='accepted';", source.Document.OperationId.ToString("D"));
        if (complete.ExecuteNonQuery() != 1) throw NotificationManualCodec.Invalid();
    }

    internal static void RequireSaved(SqliteConnection connection, SqliteTransaction transaction, NotificationWorkSnapshot source)
    {
        if (Lookup(connection, transaction, source.Document.OperationId) != source.Reference ||
            OperationRows.ReadExisting(connection, transaction, NotificationManualCodec.Identity(source))?.Status != "completed") throw NotificationManualCodec.Invalid();
        using var members = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT task_id,delivery_operation_id FROM notifications.notification_manual_deliveries WHERE operation_id=$0 ORDER BY task_id;", source.Document.OperationId.ToString("D"));
        using var rows = members.ExecuteReader(); var actual = new Dictionary<Guid, Guid>();
        while (rows.Read()) actual.Add(Guid.ParseExact(rows.GetString(0), "D"), Guid.ParseExact(rows.GetString(1), "D"));
        var queued = source.Document.Targets.Where(item => item.Disposition == "queued").ToArray();
        if (actual.Count != queued.Length || queued.Any(item => !actual.TryGetValue(item.TaskId, out var id) ||
            id != NotificationScheduledCodec.DeliveryId(source.Document.OperationId, item.TaskId))) throw NotificationManualCodec.Invalid();
    }

    internal static void RequireOrigin(SqliteConnection connection, SqliteTransaction transaction, Guid operation, Guid invocation)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT invocation_operation_id FROM notifications.notification_manual_sources WHERE operation_id=$0;", operation.ToString("D"));
        if (command.ExecuteScalar() as string != invocation.ToString("D")) throw NotificationManualCodec.Invalid();
    }

    internal static void RequireMembership(SqliteConnection connection, SqliteTransaction transaction, NotificationDeliverySnapshot delivery)
        => RequireMembership(connection, transaction, delivery.Document);
    internal static void RequireMembership(SqliteConnection connection, SqliteTransaction transaction, NotificationDeliveryDocument document)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT s.relative_path,s.size_bytes,s.sha256 FROM notifications.notification_manual_sources s JOIN notifications.notification_manual_deliveries d ON d.operation_id=s.operation_id WHERE s.operation_id=$0 AND d.task_id=$1 AND d.delivery_operation_id=$2;",
            document.AcceptanceId.ToString("D"), document.Task.TaskId.ToString("D"), document.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (document.Source.Kind != "manualInvocation" || document.Source.ExplicitSource is null || !row.Read() ||
            new BlobReference(row.GetString(0), row.GetInt64(1), row.GetString(2)) != document.Source.ExplicitSource) throw NotificationManualCodec.Invalid();
    }
}
