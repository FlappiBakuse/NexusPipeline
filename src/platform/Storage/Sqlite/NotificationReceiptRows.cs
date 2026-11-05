using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class NotificationReceiptRows
{
    internal static BlobReference? Find(SqliteConnection connection, SqliteTransaction? transaction, Guid attemptId)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM notification_receipt_sources WHERE attempt_id=$0;", attemptId.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : null;
    }

    internal static BlobReference AcceptInside(SqliteConnection connection, SqliteTransaction transaction,
        NotificationReceiptSnapshot snapshot, System.Collections.Immutable.ImmutableArray<NotificationCatalogEntry> catalogTasks,
        DateTimeOffset acceptedAt)
    {
        var document = snapshot.Document;
        if (Find(connection, transaction, document.Receipt.Facts.AttemptId) is { } prior) return prior;
        ExecutionNotificationSource.RequirePublishedRecord(connection, transaction, document.Receipt);
        RequireNoSuccessor(connection, transaction, document);
        var cursor = NotificationCatalogRows.ReadCurrent(connection, transaction);
        if (cursor is null ? document.CatalogRevision != 0 : cursor.Revision != document.CatalogRevision || cursor.Reference != document.Catalog)
            throw new InvalidOperationException("notification_revision_conflict");
        NotificationCatalogRows.RequireTaskEntries(connection, transaction, catalogTasks);
        foreach (var task in document.Tasks)
            foreach (var field in task.Secrets)
            {
                SecretFieldRows.RequireReference(connection, transaction,
                    SecretFieldKey.NotificationTask(document.InstanceId, task.TaskId, field.Field), field.Reference);
                NotificationCatalogRows.RequireFieldProvenance(connection, transaction, document.InstanceId,
                    task.TaskId, NotificationSecretFields.Name(field.Field), field.Reference, document.CatalogRevision);
                NotificationDeliveryRows.RequirePayload(connection, transaction, field, allowRetired: false);
            }
        if (document.Screenshot is { } screenshot) NotificationDeliveryRows.RequireAsset(connection, transaction, screenshot);
        _ = OperationRows.Accept(connection, transaction, Identity(snapshot), acceptedAt);
        var reference = snapshot.Reference;
        Execute(connection, transaction, "INSERT INTO notification_receipt_sources VALUES($0,$1,$2,$3,$4,$5,$6);",
            document.OperationId.ToString("D"), document.Receipt.Facts.RunId.ToString("D"),
            document.Receipt.Facts.AttemptId.ToString("D"), document.RouteCount,
            reference.RelativePath, reference.SizeBytes, reference.Sha256);
        ReceiptProgressRows.RegisterInside(connection, transaction, document.OperationId, document.RouteCount);
        foreach (var task in document.Tasks)
            foreach (var field in task.Secrets)
                Execute(connection, transaction, "INSERT INTO notification_receipt_secrets VALUES($0,$1,$2,$3,$4);",
                    document.OperationId.ToString("D"), task.TaskId.ToString("D"), NotificationSecretFields.Name(field.Field),
                    field.Reference.Revision, field.Reference.SecretId is { } secret ? secret.ToString("D") : DBNull.Value);
        Execute(connection, transaction, "UPDATE operations SET status='completed' WHERE operation_id=$0 AND status='accepted';",
            document.OperationId.ToString("D"));
        return reference;
    }

    internal static void RequireSaved(SqliteConnection connection, SqliteTransaction transaction, NotificationReceiptSnapshot snapshot)
    {
        var document = snapshot.Document;
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT s.run_id,s.attempt_id,s.route_count,s.relative_path,s.size_bytes,s.sha256,o.subject_id,o.request_digest,o.operation_kind,o.status " +
            "FROM notification_receipt_sources s JOIN operations o ON o.operation_id=s.operation_id " +
            "JOIN attempt_receipts a ON a.attempt_id=s.attempt_id JOIN operations e ON e.operation_id=a.operation_id " +
            "WHERE s.operation_id=$0 AND e.subject_id=o.subject_id AND e.operation_kind=\'execution.start\' AND e.status=\'completed\';",
            document.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != document.Receipt.Facts.RunId.ToString("D") ||
            row.GetString(1) != document.Receipt.Facts.AttemptId.ToString("D") || row.GetInt32(2) != document.RouteCount ||
            new BlobReference(row.GetString(3), row.GetInt64(4), row.GetString(5)) != snapshot.Reference ||
            row.GetString(6) != document.SubjectId || row.GetString(7) != snapshot.Reference.Sha256 ||
            row.GetString(8) != "notification.registration" || row.GetString(9) != "completed")
            throw NotificationReceiptCodec.Invalid();
        _ = ReceiptProgressRows.Require(connection, transaction, document.OperationId, document.RouteCount);
    }

    internal static void RequireReferences(SqliteConnection connection, SqliteTransaction transaction, NotificationReceiptSnapshot snapshot)
    {
        var expected = snapshot.Document.Tasks.SelectMany(task => task.Secrets.Select(field =>
            (TaskId: task.TaskId, Field: NotificationSecretFields.Name(field.Field), field.Reference, Secret: field))).ToArray();
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT task_id,field,revision,secret_id FROM notification_receipt_secrets WHERE operation_id=$0;",
            snapshot.Document.OperationId.ToString("D"));
        using var rows = command.ExecuteReader(); var seen = new HashSet<(Guid, string)>();
        while (rows.Read())
        {
            var id = Guid.ParseExact(rows.GetString(0), "D");
            var item = expected.SingleOrDefault(item => item.TaskId == id && item.Field == rows.GetString(1));
            if (id.ToString("D") != rows.GetString(0) || item.Secret is null || !seen.Add((id, rows.GetString(1))) ||
                item.Reference.Revision != rows.GetInt64(2) || item.Reference.SecretId?.ToString("D") != (rows.IsDBNull(3) ? null : rows.GetString(3)))
                throw NotificationReceiptCodec.Invalid();
            NotificationDeliveryRows.RequirePayload(connection, transaction, item.Secret, allowRetired: true);
            NotificationCatalogRows.RequireFieldProvenance(connection, transaction, snapshot.Document.InstanceId,
                id, item.Field, item.Reference, snapshot.Document.CatalogRevision);
        }
        if (seen.Count != expected.Length) throw NotificationReceiptCodec.Invalid();
    }

    private static void RequireNoSuccessor(SqliteConnection connection, SqliteTransaction transaction, NotificationReceiptDocument document)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM execution_start_sources WHERE run_id=$0 AND attempt_ordinal>$1) " +
            "OR EXISTS(SELECT 1 FROM execution_retry_intents i LEFT JOIN execution_retry_outcomes o ON o.retry_operation_id=i.operation_id " +
            "WHERE i.previous_attempt_id=$2 AND o.retry_operation_id IS NULL);",
            document.Receipt.Facts.RunId.ToString("D"), document.Receipt.Facts.Ordinal, document.Receipt.Facts.AttemptId.ToString("D"));
        if ((long)command.ExecuteScalar()! != 0) throw NotificationReceiptCodec.Invalid();
    }

    internal static OperationIdentity Identity(NotificationReceiptSnapshot snapshot) => new(snapshot.Document.OperationId,
        snapshot.Document.SubjectId, snapshot.Reference.Sha256, "notification.registration");

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params object[] values)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction, sql, values); command.ExecuteNonQuery();
    }
}
