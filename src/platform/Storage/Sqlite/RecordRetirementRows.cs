using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Records;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class RecordRetirementRows
{
    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params object[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        for (var index = 0; index < values.Length; index++)
            command.Parameters.AddWithValue("$" + index, values[index] is Guid id ? id.ToString("D") : values[index]);
        return command;
    }

    internal static void RequireFamilyInside(SqliteConnection connection, SqliteTransaction transaction, Guid runId, int count)
    {
        using var command = Command(connection, transaction,
            "SELECT count(*),coalesce(min(ordinal),0),coalesce(max(ordinal),0) FROM records.attempt_receipts WHERE run_id=$0;", runId);
        using (var rows = command.ExecuteReader())
            if (!rows.Read() || rows.GetInt32(0) != count || rows.GetInt32(1) != 1 || rows.GetInt32(2) != count) throw RecordRetirementCodec.Invalid();
        command.CommandText = "SELECT count(*) FROM main.execution_start_sources WHERE run_id=$0;";
        if ((long)command.ExecuteScalar()! != count) throw RecordRetirementCodec.Invalid();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM records.attempt_receipts a JOIN main.operation_journals j USING(journal_id) " +
            "JOIN main.operations o ON o.operation_id=a.operation_id WHERE a.run_id=$0 AND " +
            "(j.phase!='committed' OR j.operation_id!=a.operation_id OR j.plan_digest!=a.plan_digest OR o.status!='completed' OR a.recovery_outcome NOT IN(0,2))) " +
            "OR NOT EXISTS(SELECT 1 FROM records.run_records r JOIN records.attempt_receipts a ON a.attempt_id=r.attempt_id " +
            "WHERE r.run_id=$0 AND a.run_id=r.run_id AND a.ordinal=$1 AND r.attempt_ordinal=a.ordinal " +
            "AND r.engine_outcome=a.engine_outcome AND r.business_outcome=a.business_outcome " +
            "AND r.lifecycle_outcome=a.lifecycle_outcome AND r.recovery_outcome=a.recovery_outcome AND r.reason_code=a.reason_code) " +
            "OR EXISTS(SELECT 1 FROM main.execution_retry_intents i LEFT JOIN main.execution_retry_outcomes d ON d.retry_operation_id=i.operation_id " +
            "WHERE i.run_id=$0 AND d.operation_id IS NULL AND NOT EXISTS(" +
            "SELECT 1 FROM main.execution_start_sources s JOIN records.attempt_receipts n ON n.operation_id=s.operation_id " +
            "JOIN records.attempt_receipts p ON p.attempt_id=i.previous_attempt_id AND p.journal_id=i.previous_journal_id AND p.run_id=i.run_id " +
            "WHERE s.operation_id=i.next_operation_id AND s.run_id=i.run_id AND s.journal_id=i.next_journal_id " +
            "AND s.attempt_ordinal=i.next_ordinal AND s.retry_source_operation_id=p.operation_id AND p.ordinal+1=i.next_ordinal " +
            "AND n.attempt_id=i.next_attempt_id AND n.run_id=i.run_id AND n.journal_id=i.next_journal_id AND n.ordinal=i.next_ordinal)) " +
            ";";
        command.Parameters.AddWithValue("$1", count);
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("record_retirement_run_pending");
    }

    internal static void RequireSettledInside(SqliteConnection connection, SqliteTransaction transaction, Guid runId, int count)
    {
        RequireFamilyInside(connection, transaction, runId, count);
        using var command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM notifications.notification_receipt_sources s JOIN notifications.notification_receipt_progress p USING(operation_id) " +
            "WHERE s.run_id=$0 AND p.next_ordinal!=p.route_count) OR EXISTS(SELECT 1 FROM notifications.notification_receipt_sources s " +
            "JOIN notifications.notification_delivery_pending p ON p.source_operation_id=s.operation_id WHERE s.run_id=$0);", runId);
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("record_retirement_notification_pending");
    }

    internal static void RequireWritersInside(SqliteConnection connection, SqliteTransaction transaction, RecordRetirementAttempt attempt)
    {
        using var count = Command(connection, transaction,
            "SELECT count(*) FROM main.journal_writer_jobs WHERE journal_id=$0;", attempt.Receipt.JournalId);
        if ((long)count.ExecuteScalar()! != attempt.Writers.Length) throw RecordRetirementCodec.Invalid();
        foreach (var writer in attempt.Writers)
        {
            var binding = writer.Binding;
            using var command = Command(connection, transaction,
                "SELECT b.session_id,c.plan_path,c.plan_size,c.plan_digest,r.process_id,r.created_file_time,r.image_path,r.image_sha256,r.user_sid,r.elevated " +
                "FROM main.journal_writer_jobs b JOIN main.journal_writer_job_closures c USING(journal_id,writer_id,job_name) " +
                "LEFT JOIN main.journal_writer_job_roots r USING(journal_id,writer_id,job_name) WHERE b.journal_id=$0 AND b.writer_id=$1 AND b.job_name=$2;",
                binding.ParentJournalId, binding.WriterId, binding.JobName);
            using var row = command.ExecuteReader();
            if (!row.Read() || row.GetInt32(0) != binding.SessionId ||
                new BlobReference(row.GetString(1), row.GetInt64(2), row.GetString(3)) != writer.Closure) throw RecordRetirementCodec.Invalid();
            ProcessIdentity? root = row.IsDBNull(4) ? null : new(row.GetInt32(4), DateTime.FromFileTimeUtc(row.GetInt64(5)),
                row.GetString(6), row.GetString(7), row.GetString(8), row.GetBoolean(9));
            if (root != writer.Root) throw RecordRetirementCodec.Invalid();
        }
    }

    internal static BlobReference? Find(SqliteConnection connection, SqliteTransaction? transaction, Guid operationId)
    {
        using var command = Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM records.record_content_retirements WHERE operation_id=$0;", operationId);
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : null;
    }

    internal static IReadOnlyList<BlobReference> ProofsFor(SqliteConnection connection, SqliteTransaction transaction,
        Guid runId, IReadOnlyList<BlobReference> contents)
    {
        var result = new HashSet<BlobReference>();
        foreach (var content in contents.Distinct())
        {
            using var command = Command(connection, transaction,
                "SELECT r.relative_path,r.size_bytes,r.sha256,c.size_bytes,c.relative_path " +
                "FROM records.record_retired_contents c JOIN records.record_content_retirements r USING(operation_id) " +
                "WHERE c.run_id=$0 AND c.digest=$1;", runId, content.Sha256);
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                if (rows.GetInt64(3) != content.SizeBytes || rows.GetString(4) != content.RelativePath) throw RecordRetirementCodec.Invalid();
                result.Add(new(rows.GetString(0), rows.GetInt64(1), rows.GetString(2)));
            }
        }
        return result.ToArray();
    }

    internal static void RequireSavedInside(SqliteConnection connection, SqliteTransaction transaction,
        RecordRetirementDocument document, BlobReference reference)
    {
        using var command = Command(connection, transaction,
            "SELECT r.run_id,r.family_digest,r.cleanup_journal_id,o.subject_id,o.request_digest,o.operation_kind,o.status,o.initial_journal_id " +
            "FROM records.record_content_retirements r JOIN main.operations o USING(operation_id) WHERE r.operation_id=$0;", document.OperationId);
        if (Find(connection, transaction, document.OperationId) != reference) throw RecordRetirementCodec.Invalid();
        using (var row = command.ExecuteReader())
            if (!row.Read() || row.GetString(0) != document.RunId.ToString("D") || row.GetString(1) != document.Family.Sha256 ||
                row.GetString(2) != document.CleanupJournalId.ToString("D") || row.GetString(3) != document.SubjectId ||
                row.GetString(4) != Identity(document).RequestDigest || row.GetString(5) != "records.content.retire" ||
                row.GetString(6) != "completed" || row.GetString(7) != document.CleanupJournalId.ToString("D"))
                throw RecordRetirementCodec.Invalid();
        command.CommandText = "SELECT digest,size_bytes,relative_path,run_id,content_kind FROM records.record_retired_contents WHERE operation_id=$0 ORDER BY digest;";
        using var rows = command.ExecuteReader(); var index = 0;
        while (rows.Read())
        {
            if (rows.GetString(3) != document.RunId.ToString("D") || rows.GetInt32(4) != (int)document.ContentKind || index >= document.Contents.Length || document.Contents[index++] !=
                new BlobReference(rows.GetString(2), rows.GetInt64(1), rows.GetString(0))) throw RecordRetirementCodec.Invalid();
        }
        if (index != document.Contents.Length) throw RecordRetirementCodec.Invalid();
    }

    internal static OperationIdentity Identity(RecordRetirementDocument document) =>
        Identity(document.OperationId, document.SubjectId, document.RunId, document.AfterSha256, document.Limit, document.ContentKind);

    internal static OperationIdentity Identity(Guid operationId, string subjectId, Guid runId, string after, int limit,
        RetirementContentKind contentKind = RetirementContentKind.Logs) =>
        new(operationId, subjectId, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            NexusPipeline.Platform.Serialization.BoundedJson.Serialize(new { runId, afterSha256 = after, limit, contentKind }))), "records.content.retire");

    internal static void InsertInside(SqliteConnection connection, SqliteTransaction transaction,
        RecordRetirementDocument document, BlobReference reference)
    {
        using var command = Command(connection, transaction,
            "INSERT INTO records.record_content_retirements VALUES($0,$1,$2,$3,$4,$5,$6);",
            document.OperationId, document.RunId, document.Family.Sha256, document.CleanupJournalId,
            reference.RelativePath, reference.SizeBytes, reference.Sha256);
        command.ExecuteNonQuery();
        foreach (var content in document.Contents)
        {
            using var item = Command(connection, transaction,
                "INSERT INTO records.record_retired_contents VALUES($0,$1,$2,$3,$4,$5);",
                document.OperationId, document.RunId, content.Sha256, (int)document.ContentKind, content.SizeBytes, content.RelativePath);
            item.ExecuteNonQuery();
        }
        using var complete = Command(connection, transaction,
            "UPDATE main.operations SET status='completed' WHERE operation_id=$0 AND status='accepted';", document.OperationId);
        if (complete.ExecuteNonQuery() != 1) throw RecordRetirementCodec.Invalid();
    }
}
