using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal static class ScheduleMinuteRows
{
    internal static ScheduleMinuteWatermark Checkpoint(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT revision,highest_utc_minute FROM main.scheduling_watermark WHERE singleton=1;");
        ScheduleMinuteWatermark result;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw ScheduleMinuteCodec.Invalid();
            result = new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
            if (reader.Read() || result.Revision < 0 || result.HighestUtcMinute is < 0) throw ScheduleMinuteCodec.Invalid();
        }
        command.CommandText = "SELECT utc_minute FROM main.schedule_minute_claims WHERE watermark_revision=$revision;";
        command.Parameters.AddWithValue("$revision", result.Revision - 1);
        using (var reader = command.ExecuteReader())
        {
            if (result.Revision == 0 ? reader.Read() || result.HighestUtcMinute is not null :
                !reader.Read() || reader.GetInt64(0) != result.HighestUtcMinute || reader.Read())
                throw ScheduleMinuteCodec.Invalid();
        }
        return result;
    }

    internal static ScheduleMinuteDocument? ReadMinute(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, Guid instanceId)
    {
        using var command = Command(connection, transaction,
            "SELECT m.document_json,m.occurrence_count,o.subject_id,o.request_digest,o.operation_kind,o.status FROM main.schedule_minute_claims m JOIN main.operations o ON o.operation_id=m.operation_id WHERE m.operation_id=$id;");
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        string json; int count;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                using var orphan = Command(connection, transaction, "SELECT 1 FROM main.operations WHERE operation_id=$id;");
                orphan.Parameters.AddWithValue("$id", operationId.ToString("D"));
                if (orphan.ExecuteScalar() is not null) throw ScheduleMinuteCodec.Invalid();
                return null;
            }
            json = reader.GetString(0); count = reader.GetInt32(1);
            var document = ScheduleMinuteCodec.Decode(Encoding.UTF8.GetBytes(json), instanceId);
            var identity = ScheduleMinuteCodec.Identity(document);
            if (identity.OperationId != operationId || reader.GetString(2) != identity.SubjectId ||
                reader.GetString(3) != identity.RequestDigest || reader.GetString(4) != identity.Kind ||
                reader.GetString(5) != "completed" || count != document.Scan.Occurrences.Length) throw ScheduleMinuteCodec.Invalid();
        }
        var saved = ScheduleMinuteCodec.Decode(Encoding.UTF8.GetBytes(json), instanceId);
        command.CommandText = "SELECT position,occurrence_id FROM main.schedule_minute_occurrences WHERE operation_id=$id ORDER BY position;";
        using (var reader = command.ExecuteReader())
        {
            var position = 0;
            while (reader.Read())
            {
                if (position >= count || reader.GetInt32(0) != position ||
                    reader.GetString(1) != saved.Scan.Occurrences[position].OccurrenceId.ToString("D")) throw ScheduleMinuteCodec.Invalid();
                position++;
            }
            if (position != count) throw ScheduleMinuteCodec.Invalid();
        }
        foreach (var occurrence in saved.Scan.Occurrences)
        {
            var original = ReadOccurrence(connection, transaction, occurrence.OccurrenceId, instanceId);
            // A repeated DST local minute references the first accepted immutable occurrence.
            if (original.Frozen.QueueId != occurrence.QueueId || original.Frozen.TimeZoneId != occurrence.TimeZoneId || original.Frozen.LocalDate != occurrence.LocalDate ||
                original.Frozen.LocalMinute != occurrence.LocalMinute || original.Frozen.RunId != occurrence.RunId ||
                original.Frozen.StartOperationId != occurrence.StartOperationId) throw ScheduleMinuteCodec.Invalid();
        }
        return saved;
    }

    internal static ScheduledOccurrence ReadOccurrence(SqliteConnection connection, SqliteTransaction transaction, Guid occurrenceId, Guid instanceId)
    {
        using var command = Command(connection, transaction,
            "SELECT first_operation_id,position,run_id,start_operation_id,schedule_id,local_date,local_minute,trigger_utc_minute FROM main.scheduled_occurrences WHERE occurrence_id=$id;");
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        Guid source; int position; string run, start, schedule, date; int localMinute; long trigger;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw ScheduleMinuteCodec.Invalid();
            source = Guid.ParseExact(reader.GetString(0), "D"); position = reader.GetInt32(1);
            run = reader.GetString(2); start = reader.GetString(3); schedule = reader.GetString(4); date = reader.GetString(5);
            localMinute = reader.GetInt32(6); trigger = reader.GetInt64(7);
        }
        command.CommandText = "SELECT document_json FROM main.schedule_minute_claims WHERE operation_id=$source;";
        command.Parameters.AddWithValue("$source", source.ToString("D"));
        var bytes = command.ExecuteScalar() as string ?? throw ScheduleMinuteCodec.Invalid();
        var document = ScheduleMinuteCodec.Decode(Encoding.UTF8.GetBytes(bytes), instanceId);
        if (position < 0 || position >= document.Scan.Occurrences.Length ||
            document.Scan.Occurrences[position].OccurrenceId != occurrenceId) throw ScheduleMinuteCodec.Invalid();
        var proposal = document.Scan.Occurrences[position];
        var identity = ScheduleMinuteCodec.Identity(document);
        command.CommandText = "SELECT subject_id,request_digest,operation_kind,status FROM main.operations WHERE operation_id=$source;";
        using (var original = command.ExecuteReader())
            if (!original.Read() || identity.OperationId != source || original.GetString(0) != identity.SubjectId ||
                original.GetString(1) != identity.RequestDigest || original.GetString(2) != identity.Kind || original.GetString(3) != "completed")
                throw ScheduleMinuteCodec.Invalid();
        if (run != proposal.RunId.ToString("D") || start != proposal.StartOperationId.ToString("D") ||
            schedule != proposal.ScheduleId.ToString("D") || date != proposal.LocalDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ||
            localMinute != proposal.LocalMinute || trigger != proposal.TriggerUtcMinute) throw ScheduleMinuteCodec.Invalid();
        command.CommandText = "SELECT subject_id,operation_kind,status,initial_journal_id FROM main.operations WHERE operation_id=$start;";
        command.Parameters.AddWithValue("$start", proposal.StartOperationId.ToString("D"));
        using var result = command.ExecuteReader();
        var reason = ScheduleOccurrenceOutcomes.Read(connection, transaction, instanceId, proposal, document.SubjectId);
        if (!result.Read()) return new(instanceId, proposal, document.SubjectId,
            reason is null ? ScheduleOccurrencePhase.WaitingForAdmission : ScheduleOccurrencePhase.Blocked, null, null, reason);
        if (reason is not null) throw ScheduleMinuteCodec.Invalid();
        if (result.GetString(0) != document.SubjectId || result.GetString(1) != "execution.start" || result.IsDBNull(3)) throw ScheduleMinuteCodec.Invalid();
        var journalId = Guid.ParseExact(result.GetString(3), "D");
        return new(instanceId, proposal, document.SubjectId, result.GetString(2) == "accepted" ?
            ScheduleOccurrencePhase.Accepted : ScheduleOccurrencePhase.Completed, journalId, null, null);
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, ScheduleMinuteDocument document)
    {
        var identity = ScheduleMinuteCodec.Identity(document);
        using var command = Command(connection, transaction,
            "INSERT INTO main.schedule_minute_claims(operation_id,document_json,occurrence_count,watermark_revision,utc_minute) VALUES($id,$json,$count,$revision,$utc);");
        command.Parameters.AddWithValue("$id", identity.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(ScheduleMinuteCodec.Encode(document)));
        command.Parameters.AddWithValue("$count", document.Scan.Occurrences.Length);
        command.Parameters.AddWithValue("$revision", document.Scan.ExpectedWatermarkRevision);
        command.Parameters.AddWithValue("$utc", document.Scan.HighestUtcMinute); command.ExecuteNonQuery();
        for (var position = 0; position < document.Scan.Occurrences.Length; position++)
        {
            var item = document.Scan.Occurrences[position];
            command.CommandText = "INSERT INTO main.scheduled_occurrences(occurrence_id,run_id,start_operation_id,schedule_id,local_date,local_minute,trigger_utc_minute,first_operation_id,position) VALUES($occurrence,$run,$start,$schedule,$date,$minute,$utc,$id,$position) ON CONFLICT(occurrence_id) DO NOTHING;";
            command.Parameters.AddWithValue("$occurrence", item.OccurrenceId.ToString("D"));
            command.Parameters.AddWithValue("$run", item.RunId.ToString("D"));
            command.Parameters.AddWithValue("$start", item.StartOperationId.ToString("D"));
            command.Parameters.AddWithValue("$schedule", item.ScheduleId.ToString("D"));
            command.Parameters.AddWithValue("$date", item.LocalDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$minute", item.LocalMinute);
            command.Parameters.AddWithValue("$position", position); command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO main.schedule_minute_occurrences(operation_id,position,occurrence_id) VALUES($id,$position,$occurrence);";
            command.ExecuteNonQuery();
            foreach (var name in new[] { "$occurrence", "$run", "$start", "$schedule", "$date", "$minute", "$position" }) command.Parameters.RemoveAt(name);
        }
        command.CommandText = "UPDATE main.scheduling_watermark SET revision=revision+1,highest_utc_minute=$highest WHERE singleton=1 AND revision=$expected AND (highest_utc_minute IS NULL OR highest_utc_minute<$highest);";
        command.Parameters.AddWithValue("$highest", document.Scan.HighestUtcMinute);
        command.Parameters.AddWithValue("$expected", document.Scan.ExpectedWatermarkRevision);
        if (command.ExecuteNonQuery() != 1) throw new IOException("schedule_watermark_revision_conflict");
        command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$id AND operation_kind='schedule.minute';";
        if (command.ExecuteNonQuery() != 1) throw ScheduleMinuteCodec.Invalid();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    { var result = connection.CreateCommand(); result.Transaction = transaction; result.CommandText = sql; return result; }
}
