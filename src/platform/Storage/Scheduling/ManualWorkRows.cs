using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal static class ManualWorkRows
{
    internal const string Kind = "manual.work-run";
    private static readonly PendingCapacityProjection Pending = new("manual_work_pending", "operation_id", """
        FROM main.manual_work_runs candidate
        WHERE NOT EXISTS (SELECT 1 FROM main.manual_work_result r WHERE r.operation_id=candidate.operation_id)
        """, new("manual_work_runs", "operation_id"), new("manual_work_result", "operation_id"));

    internal static BlobReference? Find(SqliteConnection connection, SqliteTransaction transaction, Guid operationId)
    {
        using var command = DatabaseConnections.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM main.manual_work_runs WHERE operation_id=$0;", operationId.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : null;
    }

    internal static IReadOnlyList<(string OperationId, BlobReference Reference, long Ordinal)> RecentOrPending(
        SqliteConnection connection, SqliteTransaction transaction, string extensionId, string providerId, Guid taskId, int recentLimit)
    {
        const int maximumPending = 4096;
        using var command = DatabaseConnections.Command(connection, transaction,
            "WITH recent AS (SELECT m.operation_id FROM main.manual_work_runs m JOIN main.operations o USING(operation_id) " +
            "WHERE m.extension_id=$0 AND m.provider_id=$1 AND m.task_id=$2 ORDER BY o.rowid DESC LIMIT $3) " +
            "SELECT m.operation_id,m.relative_path,m.size_bytes,m.sha256,o.rowid FROM main.manual_work_runs m " +
            "LEFT JOIN main.operations o USING(operation_id) LEFT JOIN main.manual_work_result r USING(operation_id) " +
            "WHERE m.extension_id=$0 AND m.provider_id=$1 AND m.task_id=$2 " +
            "AND (o.rowid IS NULL OR r.operation_id IS NULL OR m.operation_id IN (SELECT operation_id FROM recent)) LIMIT $4;",
            extensionId, providerId, taskId.ToString("D"), recentLimit, maximumPending + recentLimit + 1);
        var result = new List<(string, BlobReference, long)>();
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            if (rows.IsDBNull(4) || result.Count == maximumPending + recentLimit) throw ManualWorkCodec.Invalid();
            result.Add((rows.GetString(0), new(rows.GetString(1), rows.GetInt64(2), rows.GetString(3)), rows.GetInt64(4)));
        }
        return result;
    }

    internal static IEnumerable<(string OperationId, BlobReference Reference, long Ordinal)> ForTask(
        SqliteConnection connection, SqliteTransaction transaction, string extensionId, string providerId, Guid taskId)
    {
        using var command = DatabaseConnections.Command(connection, transaction,
            "SELECT m.operation_id,m.relative_path,m.size_bytes,m.sha256,o.rowid FROM main.manual_work_runs m " +
            "LEFT JOIN main.operations o USING(operation_id) WHERE m.extension_id=$0 AND m.provider_id=$1 AND m.task_id=$2;",
            extensionId, providerId, taskId.ToString("D"));
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            if (rows.IsDBNull(4)) throw ManualWorkCodec.Invalid();
            yield return (rows.GetString(0), new(rows.GetString(1), rows.GetInt64(2), rows.GetString(3)), rows.GetInt64(4));
        }
    }

    internal static ManualWorkState Read(RuntimeInstance instance, ContractReader contracts, SqliteConnection connection,
        SqliteTransaction transaction, BlobReference reference)
    {
        var codec = new ManualWorkCodec(instance, contracts);
        return Read(codec, connection, transaction, codec.Read(reference));
    }

    internal static ManualWorkState Read(ManualWorkCodec codec, SqliteConnection connection, SqliteTransaction transaction,
        ManualWorkSnapshot source)
    {
        var operation = RequireSaved(connection, transaction, source);
        var beginRef = Leaf(connection, transaction, source.Document.OperationId, "begin");
        var resultRef = Leaf(connection, transaction, source.Document.OperationId, "result");
        var state = new ManualWorkState(source, beginRef, beginRef is null ? null : codec.ReadDocument<ManualWorkBegin>(beginRef),
            resultRef, resultRef is null ? null : codec.ReadDocument<ManualWorkResult>(resultRef));
        ManualWorkCodec.Require(state);
        if (operation.Status != (state.Result is null ? "accepted" : "completed")) throw ManualWorkCodec.Invalid();
        return state;
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, ManualWorkSnapshot source, DateTimeOffset observedAt)
    {
        if (Pending.CountInside(connection, transaction) >= 4096) throw new IOException("manual_work_capacity");
        if (!OperationRows.Accept(connection, transaction, ManualWorkCodec.Identity(source), observedAt).IsNew) throw new OperationConflictException();
        using var insert = DatabaseConnections.Command(connection, transaction, "INSERT INTO main.manual_work_runs VALUES($0,$1,$2,$3,$4,$5,$6);",
            source.Document.OperationId.ToString("D"), source.Document.ExtensionId, source.Document.ProviderId, source.Document.Task.TaskId.ToString("D"),
            source.Reference.RelativePath, source.Reference.SizeBytes, source.Reference.Sha256);
        insert.ExecuteNonQuery();
    }

    private static OperationAcceptance RequireSaved(SqliteConnection connection, SqliteTransaction transaction, ManualWorkSnapshot source)
    {
        var document = source.Document;
        var reference = source.Reference;
        using var command = DatabaseConnections.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256,extension_id,provider_id,task_id FROM main.manual_work_runs WHERE operation_id=$0;",
            document.OperationId.ToString("D"));
        using (var row = command.ExecuteReader())
            if (!row.Read() || row.GetString(0) != reference.RelativePath || row.GetInt64(1) != reference.SizeBytes || row.GetString(2) != reference.Sha256 ||
                row.GetString(3) != document.ExtensionId || row.GetString(4) != document.ProviderId ||
                row.GetString(5) != document.Task.TaskId.ToString("D")) throw ManualWorkCodec.Invalid();
        return OperationRows.ReadExisting(connection, transaction, ManualWorkCodec.Identity(source)) ?? throw ManualWorkCodec.Invalid();
    }

    internal static BlobReference? Leaf(SqliteConnection connection, SqliteTransaction transaction, Guid id, string kind)
    {
        if (kind is not ("begin" or "result")) throw new ArgumentException("manual_work_leaf_invalid");
        using var command = DatabaseConnections.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM main.manual_work_" + kind + " WHERE operation_id=$0;", id.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : null;
    }

    internal static void Append(SqliteConnection connection, SqliteTransaction transaction, ManualWorkSnapshot source, string kind, BlobReference reference)
    {
        RequireSaved(connection, transaction, source);
        if (kind is not ("begin" or "result")) throw new ArgumentException("manual_work_leaf_invalid");
        var id = source.Document.OperationId;
        if (Leaf(connection, transaction, id, kind) is { } prior)
        { if (prior != reference) throw ManualWorkCodec.Invalid(); return; }
        if (Leaf(connection, transaction, id, "result") is not null) throw ManualWorkCodec.Invalid();
        using var command = DatabaseConnections.Command(connection, transaction,
            "INSERT INTO main.manual_work_" + kind + " VALUES($0,$1,$2,$3);", id.ToString("D"), reference.RelativePath, reference.SizeBytes, reference.Sha256);
        command.ExecuteNonQuery();
        if (kind != "result") return;
        using var completed = DatabaseConnections.Command(connection, transaction,
            "UPDATE main.operations SET status='completed' WHERE operation_id=$0 AND status='accepted';", id.ToString("D"));
        if (completed.ExecuteNonQuery() != 1) throw ManualWorkCodec.Invalid();
    }
}
