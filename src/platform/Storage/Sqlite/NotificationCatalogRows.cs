using Microsoft.Data.Sqlite;
using System.Collections.Immutable;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class NotificationCatalogRows
{
    internal static NotificationCatalogCursor? ReadCurrent(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT revision,relative_path,size_bytes,sha256 FROM notification_catalog_current WHERE singleton=1;");
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        if (row.GetInt64(0) < 1) throw new IOException("notification_catalog_cursor_invalid");
        return new(row.GetInt64(0), new(row.GetString(1), row.GetInt64(2), row.GetString(3)));
    }

    internal static (BlobReference Reference, string Status)? Lookup(SqliteConnection connection,
        SqliteTransaction? transaction, OperationIdentity operation)
    {
        using var command = Command(connection, transaction,
            "SELECT o.subject_id,o.request_digest,o.operation_kind,o.status,p.relative_path,p.size_bytes,p.sha256 " +
            "FROM operations o LEFT JOIN notification_publications p ON p.operation_id=o.operation_id WHERE o.operation_id=$0;",
            operation.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        if (row.GetString(0) != operation.SubjectId || row.GetString(1) != operation.RequestDigest || row.GetString(2) != operation.Kind)
            throw new OperationConflictException();
        if (row.IsDBNull(4) || row.GetString(3) is not ("accepted" or "completed"))
            throw new IOException("notification_publication_sql_unbound");
        return (new(row.GetString(4), row.GetInt64(5), row.GetString(6)), row.GetString(3));
    }

    internal static bool AcceptInside(SqliteConnection connection, SqliteTransaction transaction,
        NotificationPublicationSnapshot publication, DateTimeOffset acceptedAt)
    {
        var document = publication.Document;
        var operation = NotificationPublicationCodec.Identity(document);
        if (Lookup(connection, transaction, operation) is not null) return false;
        using (var pending = Command(connection, transaction,
            "SELECT 1 FROM notification_publications p JOIN operations o ON o.operation_id=p.operation_id WHERE o.status='accepted' LIMIT 1;"))
            if (pending.ExecuteScalar() is not null) throw new IOException("notification_publication_pending");
        RequireBefore(connection, transaction, publication);
        RejectReusedId(connection, transaction, publication);
        _ = OperationRows.Accept(connection, transaction, operation, acceptedAt);
        var reference = publication.Reference;
        Execute(connection, transaction, "INSERT INTO notification_publications VALUES($0,$1,$2,$3,$4,$5);",
            document.OperationId.ToString("D"), reference.RelativePath, reference.SizeBytes, reference.Sha256,
            publication.Before.Catalog.Revision, publication.After.Catalog.Revision);
        foreach (var change in document.Secrets)
            Execute(connection, transaction, "INSERT INTO notification_publication_fields VALUES($0,$1,$2);",
                document.OperationId.ToString("D"), NotificationSecretFields.Name(change.Field), change.OperationId.ToString("D"));
        return true;
    }

    internal static void RequirePendingInside(SqliteConnection connection, SqliteTransaction transaction,
        NotificationPublicationSnapshot publication)
    {
        var found = Lookup(connection, transaction, NotificationPublicationCodec.Identity(publication.Document));
        if (found is not { Status: "accepted" } || found.Value.Reference != publication.Reference)
            throw new IOException("notification_publication_not_pending");
        RequireBefore(connection, transaction, publication);
    }

    internal static void RequireBefore(SqliteConnection connection, SqliteTransaction transaction, NotificationPublicationSnapshot publication)
    {
        RequirePublishedRevision(connection, transaction, publication.Before.Catalog.Revision);
        var current = ReadCurrent(connection, transaction);
        if ((current is null || current.Reference != publication.Before.Reference || current.Revision != publication.Before.Catalog.Revision) &&
            !(current is null && publication.Before.Catalog.Revision == 0 &&
              publication.Before.Catalog.Tasks.Length == 0 && publication.Before.Catalog.MaximumTasks == 10))
            throw new InvalidOperationException("notification_revision_conflict");
        foreach (var change in publication.Document.Secrets)
            SecretFieldRows.RequireReference(connection, transaction,
                SecretFieldKey.NotificationTask(publication.Document.InstanceId, publication.Document.Request.TaskId!.Value, change.Field), change.Before);
    }

    internal static void CommitCatalogInside(SqliteConnection connection, SqliteTransaction transaction,
        NotificationPublicationSnapshot publication)
    {
        var document = publication.Document;
        var found = Lookup(connection, transaction, NotificationPublicationCodec.Identity(document));
        if (found is not { Status: "accepted" } || found.Value.Reference != publication.Reference)
            throw new IOException("notification_publication_not_pending");
        RequirePublishedRevision(connection, transaction, publication.Before.Catalog.Revision);
        var current = ReadCurrent(connection, transaction);
        if ((current is null || current.Reference != publication.Before.Reference || current.Revision != publication.Before.Catalog.Revision) && !(current is null && publication.Before.Catalog.Revision == 0))
            throw new InvalidOperationException("notification_revision_conflict");
        foreach (var change in document.Secrets) RequireChildResult(connection, transaction, document, change);
        RejectReusedId(connection, transaction, publication);
        if (document.Request.Change == NotificationCatalogChange.Save && document.Request.ExpectedTaskRevision == 0)
            Execute(connection, transaction, "INSERT INTO notification_task_ids VALUES($0,$1);",
                document.Request.TaskId!.Value.ToString("D"), document.OperationId.ToString("D"));
        Execute(connection, transaction, "DELETE FROM notification_catalog_tasks;");
        foreach (var task in publication.After.Document.Tasks)
            Execute(connection, transaction, "INSERT INTO notification_catalog_tasks VALUES($0,$1,$2,$3,$4);",
                task.TaskId.ToString("D"), task.Revision, task.Definition.RelativePath, task.Definition.SizeBytes, task.Definition.Sha256);
        var reference = publication.After.Reference;
        Execute(connection, transaction,
            "INSERT INTO notification_catalog_current VALUES(1,$0,$1,$2,$3) ON CONFLICT(singleton) DO UPDATE SET " +
            "revision=excluded.revision,relative_path=excluded.relative_path,size_bytes=excluded.size_bytes,sha256=excluded.sha256;",
            publication.After.Catalog.Revision, reference.RelativePath, reference.SizeBytes, reference.Sha256);
        Execute(connection, transaction, "INSERT INTO notification_publication_outcomes VALUES($0,'published',$1);",
            document.OperationId.ToString("D"), publication.After.Catalog.Revision);
        Execute(connection, transaction, "UPDATE operations SET status='completed' WHERE operation_id=$0 AND status='accepted';",
            document.OperationId.ToString("D"));
    }

    internal static ImmutableArray<NotificationCatalogEntry> ReadTaskEntries(SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT task_id,revision,relative_path,size_bytes,sha256 FROM notification_catalog_tasks ORDER BY task_id LIMIT 100;");
        using var row = command.ExecuteReader();
        var entries = ImmutableArray.CreateBuilder<NotificationCatalogEntry>();
        while (row.Read())
        {
            var id = Guid.ParseExact(row.GetString(0), "D");
            if (entries.Count == 99 || id == Guid.Empty || id.ToString("D") != row.GetString(0) ||
                row.GetInt64(1) is < 1 or > 9007199254740991)
                throw new IOException("notification_catalog_task_row_invalid");
            var reference = new BlobReference(row.GetString(2), row.GetInt64(3), row.GetString(4));
            NotificationCatalogCodec.RequirePlan(reference);
            entries.Add(new(id, row.GetInt64(1), reference));
        }
        return entries.ToImmutable();
    }

    internal static void RequireTaskEntries(SqliteConnection connection, SqliteTransaction? transaction,
        IEnumerable<NotificationCatalogEntry> expected)
    {
        var entries = expected.OrderBy(item => item.TaskId.ToString("D"), StringComparer.Ordinal).ToArray();
        if (!ReadTaskEntries(connection, transaction).SequenceEqual(entries))
            throw new IOException("notification_catalog_task_rows_unbound");
    }

    internal static BlobReference ReadPublishedReference(SqliteConnection connection, SqliteTransaction transaction, long revision)
    {
        using var command = Command(connection, transaction,
            "SELECT p.relative_path,p.size_bytes,p.sha256 FROM notification_publications p " +
            "JOIN notification_publication_outcomes r ON r.operation_id=p.operation_id " +
            "JOIN operations o ON o.operation_id=p.operation_id " +
            "WHERE p.after_revision=$0 AND r.outcome='published' AND r.catalog_revision=$0 AND o.status='completed';", revision);
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new IOException("notification_catalog_publication_unbound");
        return new(row.GetString(0), row.GetInt64(1), row.GetString(2));
    }

    internal static void RequireFieldProvenance(SqliteConnection connection, SqliteTransaction? transaction,
        Guid instanceId, Guid taskId, string field, SecretFieldReference actual, long maximumCatalogRevision = 9007199254740991)
    {
        if (maximumCatalogRevision is < 1 or > 9007199254740991) throw new IOException("notification_catalog_revision_invalid");
        var key = SecretFieldKey.NotificationTask(instanceId, taskId, NotificationSecretFields.Parse(field));
        SecretFieldRows.RequireDatabaseKey(connection, transaction, key);
        using var command = Command(connection, transaction,
            "SELECT m.result_revision,m.result_secret_id FROM notification_publication_fields f " +
            "JOIN notification_publications p ON p.operation_id=f.parent_operation_id " +
            "JOIN notification_publication_outcomes r ON r.operation_id=p.operation_id AND r.outcome='published' " +
            "JOIN operations o ON o.operation_id=p.operation_id " +
            "JOIN operations c ON c.operation_id=f.child_operation_id " +
            "JOIN secret_mutations m ON m.operation_id=c.operation_id " +
            "WHERE f.field=$0 AND c.subject_id=$1 AND o.status='completed' AND c.status='completed' AND p.after_revision<=$2 " +
            "ORDER BY p.after_revision DESC LIMIT 1;",
            field, $"notificationTask/{instanceId:D}/{taskId:D}/{field}", maximumCatalogRevision);
        using var row = command.ExecuteReader();
        if (!row.Read() || new SecretFieldReference(row.GetInt64(0), row.IsDBNull(1) ? null : Guid.ParseExact(row.GetString(1), "D")) != actual)
            throw new IOException("notification_secret_publication_unverified");
    }

    private static void RequireChildResult(SqliteConnection connection, SqliteTransaction transaction,
        NotificationPublicationDocument document, NotificationSecretChange change)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows runtime required");
        var key = SecretFieldKey.NotificationTask(document.InstanceId, document.Request.TaskId!.Value, change.Field);
        SecretFieldRows.RequireReference(connection, transaction, key, change.After);
        RequireHistoricalResult(connection, transaction, document, change);
    }

    internal static void RequireHistoricalResult(SqliteConnection connection, SqliteTransaction? transaction,
        NotificationPublicationDocument document, NotificationSecretChange change)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows runtime required");
        var key = SecretFieldKey.NotificationTask(document.InstanceId, document.Request.TaskId!.Value, change.Field);
        var operation = SecretFieldStore.Identity(change.OperationId, key, change.Before.Revision, change.Disposition);
        using var command = Command(connection, transaction,
            "SELECT o.subject_id,o.request_digest,o.operation_kind,o.status,m.result_revision,m.result_secret_id,m.result_readable,m.replay_proof " +
            "FROM operations o JOIN secret_mutations m ON m.operation_id=o.operation_id WHERE o.operation_id=$0;",
            change.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != operation.SubjectId || row.GetString(1) != operation.RequestDigest ||
            row.GetString(2) != operation.Kind || row.GetString(3) != "completed" || row.GetInt64(4) != change.After.Revision ||
            (row.IsDBNull(5) ? null : row.GetString(5)) != change.After.SecretId?.ToString("D") ||
            row.GetBoolean(6) != change.ResultReadable || (row.IsDBNull(7) ? null : row.GetString(7)) != change.ReplayProof)
            throw new IOException("notification_secret_result_sql_unbound");
    }

    private static void RequirePublishedRevision(SqliteConnection connection, SqliteTransaction transaction, long revision)
    {
        using var command = Command(connection, transaction,
            "SELECT coalesce(max(catalog_revision),0) FROM notification_publication_outcomes WHERE outcome='published';");
        if ((long)command.ExecuteScalar()! != revision) throw new IOException("notification_catalog_history_cursor_changed");
    }

    private static void RejectReusedId(SqliteConnection connection, SqliteTransaction transaction, NotificationPublicationSnapshot publication)
    {
        if (publication.Document.Request.Change != NotificationCatalogChange.Save || publication.Document.Request.ExpectedTaskRevision != 0) return;
        using var command = Command(connection, transaction, "SELECT 1 FROM notification_task_ids WHERE task_id=$0;",
            publication.Document.Request.TaskId!.Value.ToString("D"));
        if (command.ExecuteScalar() is not null) throw new IOException("notification_task_id_retired");
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params object[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$" + index, values[index]);
        return command;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params object[] values)
    {
        using var command = Command(connection, transaction, sql, values); command.ExecuteNonQuery();
    }
}
