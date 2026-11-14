using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class TaskTestRows
{
    internal static BlobReference? Lookup(SqliteConnection connection, SqliteTransaction transaction, OperationIdentity identity)
    {
        if (OperationRows.ReadExisting(connection, transaction, identity) is null) return null;
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM notifications.notification_saved_tests WHERE operation_id=$0;", identity.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : throw TaskTestCodec.Invalid();
    }

    internal static void RequireSaved(SqliteConnection connection, SqliteTransaction transaction, TaskTestSnapshot source)
    {
        var document = source.Document;
        if (Lookup(connection, transaction, TaskTestCodec.Identity(document)) != source.Reference)
            throw TaskTestCodec.Invalid();
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT s.delivery_operation_id,o.status FROM notifications.notification_saved_tests s JOIN main.operations o ON o.operation_id=s.operation_id WHERE s.operation_id=$0;",
            document.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != TaskTestCodec.DeliveryId(document).ToString("D") ||
            row.GetString(1) != "completed") throw TaskTestCodec.Invalid();
    }

    internal static void InsertInside(SqliteConnection connection, SqliteTransaction transaction,
        TaskTestSnapshot source, DateTimeOffset created)
    {
        var document = source.Document;
        _ = OperationRows.Accept(connection, transaction, TaskTestCodec.Identity(document), created);
        using var insert = NotificationDeliveryRows.Command(connection, transaction,
            "INSERT INTO notifications.notification_saved_tests VALUES($0,$1,$2,$3,$4);", document.OperationId.ToString("D"),
            TaskTestCodec.DeliveryId(document).ToString("D"), source.Reference.RelativePath, source.Reference.SizeBytes, source.Reference.Sha256);
        insert.ExecuteNonQuery();
        using var complete = NotificationDeliveryRows.Command(connection, transaction,
            "UPDATE main.operations SET status='completed' WHERE operation_id=$0 AND status='accepted';", document.OperationId.ToString("D"));
        if (complete.ExecuteNonQuery() != 1) throw TaskTestCodec.Invalid();
    }

    internal static void RequireMembership(SqliteConnection connection, SqliteTransaction transaction, NotificationDeliverySnapshot delivery)
        => RequireMembership(connection, transaction, delivery.Document);

    internal static void RequireMembership(SqliteConnection connection, SqliteTransaction transaction, NotificationDeliveryDocument document)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM notifications.notification_saved_tests WHERE operation_id=$0 AND delivery_operation_id=$1;",
            document.AcceptanceId.ToString("D"), document.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (document.Source.Kind != "savedTaskTest" || document.Source.ExplicitSource is null || !row.Read() ||
            new BlobReference(row.GetString(0), row.GetInt64(1), row.GetString(2)) != document.Source.ExplicitSource)
            throw TaskTestCodec.Invalid();
    }
}
