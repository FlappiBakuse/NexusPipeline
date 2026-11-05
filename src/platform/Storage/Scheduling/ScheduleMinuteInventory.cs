using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal static class ScheduleMinuteInventory
{
    internal static void Verify(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var checkpoint = ScheduleMinuteRows.Checkpoint(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT operation_id,watermark_revision,utc_minute FROM schedule_minute_claims ORDER BY watermark_revision;";
        long revision = 0; long? highest = null;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetInt64(1) != revision || highest >= reader.GetInt64(2)) throw ScheduleMinuteCodec.Invalid();
                var document = ScheduleMinuteRows.ReadMinute(connection, transaction, Guid.ParseExact(reader.GetString(0), "D"), database.InstanceId)
                    ?? throw ScheduleMinuteCodec.Invalid();
                if (document.Scan.ExpectedWatermarkRevision != revision || document.Scan.HighestUtcMinute != reader.GetInt64(2))
                    throw ScheduleMinuteCodec.Invalid();
                highest = document.Scan.HighestUtcMinute; revision++;
            }
        }
        ScheduleOccurrenceOutcomes.VerifyInventory(connection, transaction);
        if (checkpoint.Revision != revision || checkpoint.HighestUtcMinute != highest) throw ScheduleMinuteCodec.Invalid();
        command.CommandText = "SELECT 1 FROM operations o LEFT JOIN schedule_minute_claims m ON m.operation_id=o.operation_id WHERE o.operation_kind='schedule.minute' AND m.operation_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw ScheduleMinuteCodec.Invalid();
        command.CommandText = "SELECT occurrence_id FROM scheduled_occurrences ORDER BY occurrence_id;";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                _ = ScheduleMinuteRows.ReadOccurrence(connection, transaction, Guid.ParseExact(reader.GetString(0), "D"), database.InstanceId);
        }
        command.CommandText = "SELECT 1 FROM scheduled_occurrences c LEFT JOIN schedule_minute_occurrences l ON l.operation_id=c.first_operation_id AND l.position=c.position AND l.occurrence_id=c.occurrence_id WHERE l.occurrence_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw ScheduleMinuteCodec.Invalid();
    }
}
