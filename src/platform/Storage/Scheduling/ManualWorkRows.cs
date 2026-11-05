using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal static class ManualWorkRows
{
    internal const string Kind = "manual.work-run";

    internal static BlobReference? Find(SqliteConnection connection, SqliteTransaction transaction, Guid operationId)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM manual_work_runs WHERE operation_id=$0;", operationId.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : null;
    }

    internal static ManualWorkState Read(RuntimeInstance instance, ContractReader contracts, SqliteConnection connection,
        SqliteTransaction transaction, BlobReference reference)
    {
        var codec = new ManualWorkCodec(instance, contracts); var source = codec.Read(reference);
        RequireSaved(connection, transaction, source);
        var beginRef = Leaf(connection, transaction, source.Document.OperationId, "begin");
        var resultRef = Leaf(connection, transaction, source.Document.OperationId, "result");
        var state = new ManualWorkState(source, beginRef, beginRef is null ? null : codec.ReadDocument<ManualWorkBegin>(beginRef),
            resultRef, resultRef is null ? null : codec.ReadDocument<ManualWorkResult>(resultRef));
        ManualWorkCodec.Require(state);
        var operation = OperationRows.ReadExisting(connection, transaction, ManualWorkCodec.Identity(source)) ?? throw ManualWorkCodec.Invalid();
        if (operation.Status != (state.Result is null ? "accepted" : "completed")) throw ManualWorkCodec.Invalid();
        return state;
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, ManualWorkSnapshot source, DateTimeOffset observedAt)
    {
        using (var capacity = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT COUNT(*) FROM manual_work_runs m LEFT JOIN manual_work_result r ON r.operation_id=m.operation_id WHERE r.operation_id IS NULL;"))
            if ((long)capacity.ExecuteScalar()! >= 4096) throw new IOException("manual_work_capacity");
        if (!OperationRows.Accept(connection, transaction, ManualWorkCodec.Identity(source), observedAt).IsNew) throw new OperationConflictException();
        using var insert = NotificationDeliveryRows.Command(connection, transaction, "INSERT INTO manual_work_runs VALUES($0,$1,$2,$3,$4,$5,$6);",
            source.Document.OperationId.ToString("D"), source.Document.ExtensionId, source.Document.ProviderId, source.Document.Task.TaskId.ToString("D"),
            source.Reference.RelativePath, source.Reference.SizeBytes, source.Reference.Sha256);
        insert.ExecuteNonQuery();
    }

    internal static void RequireSaved(SqliteConnection connection, SqliteTransaction transaction, ManualWorkSnapshot source)
    {
        if (Find(connection, transaction, source.Document.OperationId) != source.Reference) throw ManualWorkCodec.Invalid();
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT extension_id,provider_id,task_id FROM manual_work_runs WHERE operation_id=$0;", source.Document.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != source.Document.ExtensionId || row.GetString(1) != source.Document.ProviderId ||
            row.GetString(2) != source.Document.Task.TaskId.ToString("D")) throw ManualWorkCodec.Invalid();
        _ = OperationRows.ReadExisting(connection, transaction, ManualWorkCodec.Identity(source)) ?? throw ManualWorkCodec.Invalid();
    }

    internal static BlobReference? Leaf(SqliteConnection connection, SqliteTransaction transaction, Guid id, string kind)
    {
        if (kind is not ("begin" or "result")) throw new ArgumentException("manual_work_leaf_invalid");
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM manual_work_" + kind + " WHERE operation_id=$0;", id.ToString("D"));
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
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "INSERT INTO manual_work_" + kind + " VALUES($0,$1,$2,$3);", id.ToString("D"), reference.RelativePath, reference.SizeBytes, reference.Sha256);
        command.ExecuteNonQuery();
        if (kind != "result") return;
        using var completed = NotificationDeliveryRows.Command(connection, transaction,
            "UPDATE operations SET status='completed' WHERE operation_id=$0 AND status='accepted';", id.ToString("D"));
        if (completed.ExecuteNonQuery() != 1) throw ManualWorkCodec.Invalid();
    }
}
