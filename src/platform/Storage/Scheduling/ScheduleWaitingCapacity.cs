using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal static class ScheduleWaitingCapacity
{
    internal const int Maximum = 4096;
    private const string PendingOccurrences = """
        FROM main.scheduled_occurrences candidate
        WHERE NOT EXISTS (SELECT 1 FROM main.operations o WHERE o.operation_id=candidate.start_operation_id)
        AND NOT EXISTS (SELECT 1 FROM main.schedule_occurrence_outcomes b WHERE b.occurrence_id=candidate.occurrence_id)
        """;
    private static readonly PendingCapacityProjection Pending = new("schedule_waiting", "occurrence_id", PendingOccurrences,
        new("scheduled_occurrences", "occurrence_id", MembershipColumn: "start_operation_id"),
        new("schedule_occurrence_outcomes", "occurrence_id"),
        new("operations", "operation_id", "SELECT occurrence_id FROM main.scheduled_occurrences WHERE start_operation_id="));

    internal static long CountInside(SqliteConnection connection, SqliteTransaction transaction) =>
        Pending.CountInside(connection, transaction) + CountProviderInside(connection, transaction);

    internal static IReadOnlyList<Guid> ReadPageInside(SqliteConnection connection, SqliteTransaction transaction, int limit)
    {
        if (Pending.CountInside(connection, transaction) == 0) return [];
        using var command = DatabaseConnections.Command(connection, transaction, """
            SELECT c.occurrence_id FROM temp.schedule_waiting p
            CROSS JOIN main.scheduled_occurrences c ON c.occurrence_id=p.occurrence_id
            ORDER BY c.trigger_utc_minute,c.occurrence_id LIMIT $0;
            """, limit);
        var ids = new List<Guid>();
        using var rows = command.ExecuteReader();
        while (rows.Read()) ids.Add(Guid.ParseExact(rows.GetString(0), "D"));
        return ids;
    }

    internal static IReadOnlyList<(Guid Id, Guid? Minute)> ReadActivityCandidatesInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        _ = Pending.CountInside(connection, transaction);
        using var command = DatabaseConnections.Command(connection, transaction, """
            SELECT c.occurrence_id,0,c.first_operation_id FROM temp.schedule_waiting p
            CROSS JOIN main.scheduled_occurrences c ON c.occurrence_id=p.occurrence_id
            UNION ALL
            SELECT c.occurrence_id,1,NULL FROM main.startup_queue_occurrences c
            LEFT JOIN main.operations o ON o.operation_id=c.start_operation_id
            LEFT JOIN main.startup_queue_outcomes b ON b.occurrence_id=c.occurrence_id
            WHERE o.operation_id IS NULL AND b.occurrence_id IS NULL ORDER BY 1 LIMIT 8193;
            """);
        var ids = new List<(Guid, Guid?)>();
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            if (ids.Count == 8192) throw new IOException("schedule_pending_capacity");
            var minute = rows.GetInt32(1) == 1 ? (Guid?)null : Guid.ParseExact(rows.GetString(2), "D");
            ids.Add((StartupQueueRows.ParseId(rows.GetString(0)), minute));
        }
        return ids;
    }

    internal static void VerifyInventory(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var ordinary = DatabaseConnections.Command(connection, transaction, $"SELECT count(*) {PendingOccurrences};");
        if ((long)ordinary.ExecuteScalar()! + CountProviderInside(connection, transaction) > Maximum)
            throw new IOException("schedule_waiting_capacity");
    }

    private static long CountProviderInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = DatabaseConnections.Command(connection, transaction, "SELECT count(*) FROM main.scheduled_provider_pending;");
        return (long)command.ExecuteScalar()!;
    }
}
