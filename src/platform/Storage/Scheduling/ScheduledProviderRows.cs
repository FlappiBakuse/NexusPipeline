using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using System.Globalization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal static class ScheduledProviderRows
{
    internal const string Kind = "schedule.provider-invoke";
    internal static OperationIdentity Identity(ScheduledProviderSnapshot source) => new(source.Document.OperationId,
        "host:scheduled-provider", source.Reference.Sha256, Kind);

    internal static BlobReference? Find(SqliteConnection connection, SqliteTransaction? transaction, Guid occurrence)
    {
        using var command = DatabaseConnections.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM main.scheduled_provider_occurrences WHERE occurrence_id=$0;", occurrence.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : null;
    }

    internal static void InsertInside(SqliteConnection connection, SqliteTransaction transaction,
        ScheduledProviderSnapshot source, DateTimeOffset observedAt)
    {
        var document = source.Document;
        var minute = ScheduleMinuteRows.ReadMinute(connection, transaction, document.MinuteOperationId, document.InstanceId) ??
            throw ScheduledProviderCodec.Invalid();
        if (minute.Scan.HighestUtcMinute != document.TriggerUtcMinute || minute.ObservedAtUtc != document.ObservedAtUtc)
            throw ScheduledProviderCodec.Invalid();
        _ = OperationRows.Accept(connection, transaction, Identity(source), observedAt);
        using var command = DatabaseConnections.Command(connection, transaction,
            "INSERT INTO main.scheduled_provider_occurrences VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10);",
            document.Invocation.OccurrenceId.ToString("D"), document.OperationId.ToString("D"), document.MinuteOperationId.ToString("D"),
            document.ExtensionId, document.ProviderId, document.Task.TaskId.ToString("D"), document.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            document.Task.LocalMinute, source.Reference.RelativePath, source.Reference.SizeBytes, source.Reference.Sha256);
        command.ExecuteNonQuery();
        using var pending = DatabaseConnections.Command(connection, transaction,
            "INSERT INTO main.scheduled_provider_pending VALUES($0,$1);", document.Invocation.OccurrenceId.ToString("D"), document.TriggerUtcMinute);
        pending.ExecuteNonQuery();
    }

    internal static void RequireSaved(SqliteConnection connection, SqliteTransaction transaction, ScheduledProviderSnapshot source)
    {
        var document = source.Document;
        var minute = ScheduleMinuteRows.ReadMinute(connection, transaction, document.MinuteOperationId, document.InstanceId) ??
            throw ScheduledProviderCodec.Invalid();
        if (minute.Scan.HighestUtcMinute != document.TriggerUtcMinute || minute.ObservedAtUtc != document.ObservedAtUtc)
            throw ScheduledProviderCodec.Invalid();
        if (Find(connection, transaction, document.Invocation.OccurrenceId) != source.Reference ||
            OperationRows.ReadExisting(connection, transaction, Identity(source)) is null) throw ScheduledProviderCodec.Invalid();
        using var command = DatabaseConnections.Command(connection, transaction,
            "SELECT operation_id,minute_operation_id,extension_id,provider_id,task_id,local_date,local_minute FROM main.scheduled_provider_occurrences WHERE occurrence_id=$0;",
            document.Invocation.OccurrenceId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != document.OperationId.ToString("D") || row.GetString(1) != document.MinuteOperationId.ToString("D") ||
            row.GetString(2) != document.ExtensionId || row.GetString(3) != document.ProviderId || row.GetString(4) != document.Task.TaskId.ToString("D") ||
            row.GetString(5) != document.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) || row.GetInt32(6) != document.Task.LocalMinute)
            throw ScheduledProviderCodec.Invalid();
    }

    internal static BlobReference? Leaf(SqliteConnection connection, SqliteTransaction? transaction, Guid occurrence, string leaf)
    {
        if (leaf is not ("begin" or "close" or "result")) throw new ArgumentException("scheduled_provider_leaf_invalid");
        using var command = DatabaseConnections.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM main.scheduled_provider_" + leaf + " WHERE occurrence_id=$0;", occurrence.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : null;
    }

    internal static void AppendLeafInside(SqliteConnection connection, SqliteTransaction transaction, ScheduledProviderSnapshot source,
        string leaf, BlobReference reference)
    {
        RequireSaved(connection, transaction, source);
        if (Leaf(connection, transaction, source.Document.Invocation.OccurrenceId, leaf) is { } existing)
        { if (existing != reference) throw ScheduledProviderCodec.Invalid(); return; }
        if (Leaf(connection, transaction, source.Document.Invocation.OccurrenceId, "result") is not null) throw ScheduledProviderCodec.Invalid();
        using var command = DatabaseConnections.Command(connection, transaction,
            "INSERT INTO main.scheduled_provider_" + leaf + " VALUES($0,$1,$2,$3);", source.Document.Invocation.OccurrenceId.ToString("D"),
            reference.RelativePath, reference.SizeBytes, reference.Sha256);
        command.ExecuteNonQuery();
        if (leaf == "result")
        {
            using var completed = DatabaseConnections.Command(connection, transaction,
                "UPDATE main.operations SET status='completed' WHERE operation_id=$0 AND status='accepted';", source.Document.OperationId.ToString("D"));
            if (completed.ExecuteNonQuery() != 1) throw ScheduledProviderCodec.Invalid();
            using var pending = DatabaseConnections.Command(connection, transaction,
                "DELETE FROM main.scheduled_provider_pending WHERE occurrence_id=$0;", source.Document.Invocation.OccurrenceId.ToString("D"));
            if (pending.ExecuteNonQuery() != 1) throw ScheduledProviderCodec.Invalid();
        }
    }

    internal static IReadOnlyList<BlobReference> ReadUnfinished(SqliteConnection connection, SqliteTransaction transaction, int maximum)
    {
        if (maximum is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximum));
        using var command = DatabaseConnections.Command(connection, transaction,
            "SELECT c.relative_path,c.size_bytes,c.sha256 FROM main.scheduled_provider_pending p JOIN main.scheduled_provider_occurrences c ON c.occurrence_id=p.occurrence_id ORDER BY p.trigger_utc_minute,p.occurrence_id LIMIT $0;", maximum);
        using var rows = command.ExecuteReader(); var result = new List<BlobReference>();
        while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetInt64(1), rows.GetString(2)));
        return result;
    }

    internal static void RequireCapacity(SqliteConnection connection, SqliteTransaction transaction, int additions)
    {
        if (additions is < 0 or > 4096) throw ScheduledProviderCodec.Invalid();
        using var command = DatabaseConnections.Command(connection, transaction,
            "SELECT (SELECT COUNT(*) FROM main.scheduled_provider_pending)+(SELECT COUNT(*) FROM main.scheduled_occurrences c LEFT JOIN main.operations o ON o.operation_id=c.start_operation_id LEFT JOIN main.schedule_occurrence_outcomes b ON b.occurrence_id=c.occurrence_id WHERE o.operation_id IS NULL AND b.occurrence_id IS NULL);");
        if ((long)command.ExecuteScalar()! + additions > 4096) throw new IOException("schedule_waiting_capacity");
    }

    internal static void RequireOpen(SqliteConnection connection, SqliteTransaction transaction, ScheduledProviderSnapshot source, bool requireBegin)
    {
        RequireSaved(connection, transaction, source);
        var id = source.Document.Invocation.OccurrenceId;
        if (Leaf(connection, transaction, id, "close") is not null || Leaf(connection, transaction, id, "result") is not null ||
            requireBegin && Leaf(connection, transaction, id, "begin") is null) throw new IOException("scheduled_provider_dispatch_closed");
        var status = OperationRows.ReadExisting(connection, transaction, Identity(source)) ?? throw ScheduledProviderCodec.Invalid();
        if (status.Status != "accepted") throw ScheduledProviderCodec.Invalid();
    }
}
