using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class NotificationScheduledRows
{
    internal static BlobReference? Lookup(SqliteConnection connection, SqliteTransaction transaction, Guid operation)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM notification_scheduled_sources WHERE operation_id=$0;", operation.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : null;
    }

    internal static void InsertInside(SqliteConnection connection, SqliteTransaction transaction,
        NotificationWorkSnapshot source, Guid occurrence, DateTimeOffset created)
    {
        _ = OperationRows.Accept(connection, transaction, NotificationScheduledCodec.Identity(source), created);
        using var insert = NotificationDeliveryRows.Command(connection, transaction,
            "INSERT INTO notification_scheduled_sources VALUES($0,$1,$2,$3,$4);", source.Document.OperationId.ToString("D"),
            occurrence.ToString("D"), source.Reference.RelativePath, source.Reference.SizeBytes, source.Reference.Sha256);
        insert.ExecuteNonQuery();
        foreach (var target in source.Document.Targets.Where(item => item.Disposition == "queued"))
        {
            using var member = NotificationDeliveryRows.Command(connection, transaction,
                "INSERT INTO notification_scheduled_deliveries VALUES($0,$1,$2);", source.Document.OperationId.ToString("D"),
                target.TaskId.ToString("D"), NotificationScheduledCodec.DeliveryId(source.Document.OperationId, target.TaskId).ToString("D"));
            member.ExecuteNonQuery();
        }
        using var complete = NotificationDeliveryRows.Command(connection, transaction,
            "UPDATE operations SET status='completed' WHERE operation_id=$0 AND status='accepted';", source.Document.OperationId.ToString("D"));
        if (complete.ExecuteNonQuery() != 1) throw NotificationScheduledCodec.Invalid();
    }

    internal static void RequireSaved(SqliteConnection connection, SqliteTransaction transaction, NotificationWorkSnapshot source)
    {
        if (Lookup(connection, transaction, source.Document.OperationId) != source.Reference ||
            OperationRows.ReadExisting(connection, transaction, NotificationScheduledCodec.Identity(source))?.Status != "completed")
            throw NotificationScheduledCodec.Invalid();
        using var members = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT task_id,delivery_operation_id FROM notification_scheduled_deliveries WHERE operation_id=$0 ORDER BY task_id;",
            source.Document.OperationId.ToString("D"));
        using var rows = members.ExecuteReader(); var actual = new Dictionary<Guid, Guid>();
        while (rows.Read()) actual.Add(Guid.ParseExact(rows.GetString(0), "D"), Guid.ParseExact(rows.GetString(1), "D"));
        var queued = source.Document.Targets.Where(item => item.Disposition == "queued").ToArray();
        if (actual.Count != queued.Length || queued.Any(item => !actual.TryGetValue(item.TaskId, out var id) ||
            id != NotificationScheduledCodec.DeliveryId(source.Document.OperationId, item.TaskId))) throw NotificationScheduledCodec.Invalid();
    }

    internal static void RequireOccurrence(SqliteConnection connection, SqliteTransaction transaction,
        Guid operation, Guid occurrence)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT occurrence_id FROM notification_scheduled_sources WHERE operation_id=$0;", operation.ToString("D"));
        if (command.ExecuteScalar() as string != occurrence.ToString("D")) throw NotificationScheduledCodec.Invalid();
    }

    internal static void RequireMembership(SqliteConnection connection, SqliteTransaction transaction, NotificationDeliverySnapshot delivery) =>
        RequireMembership(connection, transaction, delivery.Document);
    internal static void RequireMembership(SqliteConnection connection, SqliteTransaction transaction, NotificationDeliveryDocument document)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT s.relative_path,s.size_bytes,s.sha256 FROM notification_scheduled_sources s JOIN notification_scheduled_deliveries d ON d.operation_id=s.operation_id WHERE s.operation_id=$0 AND d.task_id=$1 AND d.delivery_operation_id=$2;",
            document.AcceptanceId.ToString("D"), document.Task.TaskId.ToString("D"), document.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (document.Source.Kind != "scheduledInvocation" || document.Source.ExplicitSource is null || !row.Read() ||
            new BlobReference(row.GetString(0), row.GetInt64(1), row.GetString(2)) != document.Source.ExplicitSource)
            throw NotificationScheduledCodec.Invalid();
    }
}
