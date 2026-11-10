using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class NotificationDeliveryRows
{
    internal static void AcceptInside(SqliteConnection connection, SqliteTransaction transaction,
        NotificationDeliverySnapshot snapshot, DateTimeOffset acceptedAt, Guid generation)
        => AcceptCore(connection, transaction, snapshot, acceptedAt, generation, null, false);

    internal static void AcceptRegisteredInside(SqliteConnection connection, SqliteTransaction transaction,
        NotificationDeliverySnapshot snapshot, NotificationReceiptSnapshot registration, DateTimeOffset acceptedAt, Guid generation)
        => AcceptCore(connection, transaction, snapshot, acceptedAt, generation, registration, false);

    internal static void AcceptExplicitInside(SqliteConnection connection, SqliteTransaction transaction,
        NotificationDeliverySnapshot snapshot, DateTimeOffset acceptedAt, Guid generation) =>
        AcceptCore(connection, transaction, snapshot, acceptedAt, generation, null, true);

    private static void AcceptCore(SqliteConnection connection, SqliteTransaction transaction,
        NotificationDeliverySnapshot snapshot, DateTimeOffset acceptedAt, Guid generation, NotificationReceiptSnapshot? registration, bool explicitSource)
    {
        var document = snapshot.Document;
        if (!explicitSource && document.Source.Kind != "executionReceipt") throw TaskTestCodec.Invalid();
        var operation = Identity(snapshot);
        if (OperationRows.ReadExisting(connection, transaction, operation) is not null)
        {
            RequireSaved(connection, transaction, snapshot);
            return;
        }
        RequireSource(connection, transaction, document);
        if (explicitSource)
        {
            NotificationDeliveryOrigins.RequireMembership(connection, transaction, snapshot);
            if (document.Source.Kind is not ("savedTaskTest" or "scheduledInvocation" or "manualInvocation")) throw TaskTestCodec.Invalid();
        }
        else if (registration is null)
        {
            if (document.Registration is not null) throw NotificationReceiptCodec.Invalid();
            var current = NotificationCatalogRows.ReadCurrent(connection, transaction);
            if (current is null || current.Reference != document.Catalog || current.Revision != document.CatalogRevision)
                throw new InvalidOperationException("notification_revision_conflict");
        }
        else
        {
            NotificationReceiptRows.RequireSaved(connection, transaction, registration);
            NotificationReceiptRows.RequireReferences(connection, transaction, registration);
            if (document.Registration != registration.Reference || document.AcceptanceId != registration.Document.OperationId ||
                document.Source.Receipt != registration.Document.Receipt || document.Catalog != registration.Document.Catalog ||
                document.CatalogRevision != registration.Document.CatalogRevision || document.SubjectId != registration.Document.SubjectId)
                throw NotificationReceiptCodec.Invalid();
            var fields = registration.Document.Tasks.SingleOrDefault(item => item.TaskId == document.Task.TaskId);
            if (fields is null || !fields.Secrets.SequenceEqual(document.Secrets)) throw NotificationReceiptCodec.Invalid();
        }
        foreach (var field in document.Secrets)
        {
            var key = SecretFieldKey.NotificationTask(document.InstanceId, document.Task.TaskId, field.Field);
            if (registration is null && !explicitSource) SecretFieldRows.RequireReference(connection, transaction, key, field.Reference);
            NotificationCatalogRows.RequireFieldProvenance(connection, transaction, document.InstanceId,
                document.Task.TaskId, NotificationSecretFields.Name(field.Field), field.Reference, document.CatalogRevision);
            RequirePayload(connection, transaction, field, allowRetired: registration is not null || explicitSource);
        }
        if (document.Attachment is { } attachment) RequireAsset(connection, transaction, attachment);
        _ = OperationRows.Accept(connection, transaction, operation, acceptedAt);
        var reference = snapshot.Reference;
        Execute(connection, transaction, "INSERT INTO notification_deliveries VALUES($0,$1,$2,$3,$4,$5,$6);",
            document.OperationId.ToString("D"), document.AcceptanceId.ToString("D"),
            document.Source.Kind == "executionReceipt" ? document.Source.Receipt.Facts.AttemptId.ToString("D") : DBNull.Value, document.Task.TaskId.ToString("D"),
            reference.RelativePath, reference.SizeBytes, reference.Sha256);
        foreach (var field in document.Secrets)
            Execute(connection, transaction, "INSERT INTO notification_delivery_secrets VALUES($0,$1,$2,$3);",
                document.OperationId.ToString("D"), NotificationSecretFields.Name(field.Field), field.Reference.Revision,
                field.Reference.SecretId is { } id ? id.ToString("D") : DBNull.Value);
        if (document.Attachment is { } asset)
            Execute(connection, transaction, "INSERT INTO notification_delivery_assets VALUES($0,$1,$2,$3,$4);",
                document.OperationId.ToString("D"), asset.RunId.ToString("D"), asset.Sha256, (int)asset.Kind, asset.SizeBytes);
        AppendInside(connection, transaction, snapshot, new("nexus.notification.delivery-transition", 1,
            document.OperationId, 0, NotificationDeliveryState.Queued, generation, null, "notification_queued", null));
    }

    internal static void RequireSource(SqliteConnection connection, SqliteTransaction? transaction,
        NotificationDeliveryDocument document)
    {
        if (document.Source.Kind != "executionReceipt")
        {
            if (document.Source.Kind is not ("savedTaskTest" or "scheduledInvocation" or "manualInvocation")) throw TaskTestCodec.Invalid();
            if (document.Source.Kind == "savedTaskTest")
                TaskTestRows.RequireMembership(connection, transaction ?? throw TaskTestCodec.Invalid(), document);
            else if (document.Source.Kind == "scheduledInvocation")
                NotificationScheduledRows.RequireMembership(connection, transaction ?? throw NotificationScheduledCodec.Invalid(), document);
            else NotificationManualRows.RequireMembership(connection, transaction ?? throw NotificationManualCodec.Invalid(), document);
            return;
        }
        var receipt = document.Source.Receipt;
        if (AttemptReceiptStore.ReadInside(connection, transaction, receipt.Facts.AttemptId) != receipt)
            throw new IOException("notification_delivery_receipt_unbound");
        using var command = Command(connection, transaction,
            "SELECT j.phase,j.plan_digest,o.status,o.operation_kind,o.subject_id FROM operation_journals j " +
            "JOIN operations o ON o.initial_journal_id=j.journal_id WHERE j.journal_id=$0 AND o.operation_id=$1;",
            receipt.JournalId.ToString("D"), receipt.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != "committed" || row.GetString(1) != receipt.PlanDigest ||
            row.GetString(2) != "completed" || row.GetString(3) != "execution.start" || row.GetString(4) != document.SubjectId)
            throw new IOException("notification_delivery_receipt_unbound");
    }

    internal static void RequireSaved(SqliteConnection connection, SqliteTransaction? transaction,
        NotificationDeliverySnapshot snapshot)
    {
        var document = snapshot.Document;
        using var command = Command(connection, transaction,
            "SELECT acceptance_id,source_attempt_id,task_id,relative_path,size_bytes,sha256 " +
            "FROM notification_deliveries WHERE operation_id=$0;", document.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != document.AcceptanceId.ToString("D") ||
            (row.IsDBNull(1) ? null : row.GetString(1)) != (document.Source.Kind == "executionReceipt" ? document.Source.Receipt.Facts.AttemptId.ToString("D") : null) ||
            row.GetString(2) != document.Task.TaskId.ToString("D") ||
            new BlobReference(row.GetString(3), row.GetInt64(4), row.GetString(5)) != snapshot.Reference)
            throw new IOException("notification_delivery_sql_unbound");
    }

    internal static NotificationDeliveryTransition ReadState(SqliteConnection connection,
        SqliteTransaction? transaction, Guid operationId)
    {
        using var command = Command(connection, transaction,
            "SELECT ordinal,document_json FROM notification_delivery_transitions WHERE operation_id=$0 ORDER BY ordinal;",
            operationId.ToString("D"));
        using var rows = command.ExecuteReader();
        NotificationDeliveryTransition? prior = null;
        while (rows.Read())
        {
            var saved = NotificationCatalogCodec.Decode<NotificationDeliveryTransition>(
                System.Text.Encoding.UTF8.GetBytes(rows.GetString(1)));
            if (saved.OperationId != operationId || saved.Ordinal != rows.GetInt32(0))
                throw new IOException("notification_delivery_transition_unbound");
            NotificationDeliveryStates.RequireSuccessor(prior, saved);
            prior = saved;
        }
        return prior ?? throw new IOException("notification_delivery_state_missing");
    }

    internal static void AppendInside(SqliteConnection connection, SqliteTransaction transaction,
        NotificationDeliverySnapshot snapshot, NotificationDeliveryTransition next)
    {
        RequireSaved(connection, transaction, snapshot);
        NotificationDeliveryTransition? before = null;
        using (var count = Command(connection, transaction,
            "SELECT count(*) FROM notification_delivery_transitions WHERE operation_id=$0;", next.OperationId.ToString("D")))
            if ((long)count.ExecuteScalar()! != 0) before = ReadState(connection, transaction, next.OperationId);
        if (before == next) { RequirePendingProjection(connection, transaction, next, NotificationDeliveryOrigins.SourceOperation(snapshot.Document)); return; }
        NotificationDeliveryStates.RequireSuccessor(before, next);
        if (next.OperationId != snapshot.Document.OperationId) throw new IOException("notification_delivery_transition_unbound");
        Execute(connection, transaction, "INSERT INTO notification_delivery_transitions VALUES($0,$1,$2);",
            next.OperationId.ToString("D"), next.Ordinal,
            System.Text.Encoding.UTF8.GetString(NotificationCatalogCodec.Encode(next)));
        if (NotificationDeliveryStates.ReleasesReferences(next.State))
            Execute(connection, transaction, "DELETE FROM notification_delivery_pending WHERE operation_id=$0;", next.OperationId.ToString("D"));
        else
            Execute(connection, transaction, "INSERT INTO notification_delivery_pending VALUES($0,$1,$2) ON CONFLICT(operation_id) DO UPDATE SET state=excluded.state;",
                next.OperationId.ToString("D"), NotificationDeliveryOrigins.SourceOperation(snapshot.Document) is { } source ? source.ToString("D") : DBNull.Value,
                next.State.ToString().ToLowerInvariant());
        if (next.State is not (NotificationDeliveryState.Queued or NotificationDeliveryState.Sending))
            Execute(connection, transaction, "UPDATE operations SET status='completed' WHERE operation_id=$0 AND status='accepted';",
                next.OperationId.ToString("D"));
    }

    internal static void RequirePendingProjection(SqliteConnection connection, SqliteTransaction? transaction,
        NotificationDeliveryTransition state, Guid? source = null)
    {
        using var command = Command(connection, transaction,
            "SELECT state,source_operation_id FROM notification_delivery_pending WHERE operation_id=$0;", state.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        var present = row.Read();
        if (NotificationDeliveryStates.ReleasesReferences(state.State))
        { if (present) throw new IOException("notification_delivery_pending_unbound"); return; }
        if (!present || row.GetString(0) != state.State.ToString().ToLowerInvariant() ||
            (row.IsDBNull(1) ? null : row.GetString(1)) != source?.ToString("D"))
            throw new IOException("notification_delivery_pending_unbound");
    }

    internal static void RequirePayload(SqliteConnection connection, SqliteTransaction? transaction,
        NotificationDeliverySecret field, bool allowRetired)
    {
        if (field.Payload is { } expected) RequirePayloadMetadata(connection, transaction, expected, allowRetired);
    }

    internal static void RequirePayloadMetadata(SqliteConnection connection, SqliteTransaction? transaction,
        SecretPayloadIdentity expected, bool allowRetired)
    {
        using var command = Command(connection, transaction,
            "SELECT protection_scheme,owner_sid,digest,size_bytes,state FROM secret_payloads WHERE secret_id=$0;",
            expected.SecretId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || new SecretPayloadIdentity(expected.SecretId, row.GetString(0), row.GetString(1),
            row.GetString(2), row.GetInt64(3)) != expected ||
            row.GetString(4) != "committed" && !(allowRetired && row.GetString(4) == "retired"))
            throw new IOException("notification_delivery_secret_unbound");
    }

    internal static void RequireAsset(SqliteConnection connection, SqliteTransaction? transaction, RecordAsset asset)
    {
        using var command = Command(connection, transaction,
            "SELECT b.size_bytes,b.relative_path FROM record_assets a JOIN blobs b ON a.digest=b.digest " +
            "WHERE a.run_id=$0 AND a.digest=$1 AND a.kind=$2;", asset.RunId.ToString("D"), asset.Sha256, (int)asset.Kind);
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetInt64(0) != asset.SizeBytes || row.GetString(1) != $"blobs/content/{asset.Sha256}.bin")
            throw new IOException("notification_delivery_attachment_unbound");
    }

    internal static OperationIdentity Identity(NotificationDeliverySnapshot snapshot) => new(snapshot.Document.OperationId,
        snapshot.Document.SubjectId, snapshot.Reference.Sha256, "notification.delivery");

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params object[] values) => DatabaseConnections.Command(connection, transaction, sql, values);

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params object[] values)
    {
        using var command = Command(connection, transaction, sql, values); command.ExecuteNonQuery();
    }
}
