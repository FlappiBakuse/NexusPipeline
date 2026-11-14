using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal static class ScheduleOccurrenceOutcomes
{
    private const string Kind = "schedule.occurrence.block";
    internal static void RequireReason(string reason)
    {
        if (reason is not ("schedule_source_unavailable" or "schedule_source_cancelled" or "schedule_budget_continuity_unknown"))
            throw new ArgumentException("schedule_block_reason_invalid");
    }

    private static OperationIdentity Identity(Guid instance, ScheduledOccurrenceProposal frozen, string subject, string reason)
    {
        RequireReason(reason);
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("nexus.schedule.block/1/" + instance.ToString("D") + "/" +
            frozen.OccurrenceId.ToString("D"))).AsSpan(0, 16), bigEndian: true);
        var bytes = BoundedJson.Serialize(new { instanceId = instance, frozen, subject, reason }, ScheduleMinuteCodec.Options);
        return new(id, subject, Convert.ToHexStringLower(SHA256.HashData(bytes)), Kind);
    }

    internal static string? Read(SqliteConnection connection, SqliteTransaction transaction, Guid instance,
        ScheduledOccurrenceProposal frozen, string subject)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT b.operation_id,b.reason_code,o.subject_id,o.request_digest,o.operation_kind,o.status,o.initial_journal_id FROM main.schedule_occurrence_outcomes b LEFT JOIN main.operations o ON o.operation_id=b.operation_id WHERE b.occurrence_id=$occurrence;";
        command.Parameters.AddWithValue("$occurrence", frozen.OccurrenceId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var reason = reader.GetString(1); var identity = Identity(instance, frozen, subject, reason);
        if (reader.IsDBNull(2) || reader.GetString(0) != identity.OperationId.ToString("D") || reader.GetString(2) != identity.SubjectId ||
            reader.GetString(3) != identity.RequestDigest || reader.GetString(4) != Kind || reader.GetString(5) != "completed" ||
            !reader.IsDBNull(6)) throw ScheduleMinuteCodec.Invalid();
        return reason;
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, ScheduledOccurrence occurrence,
        string reason, DateTimeOffset now)
    {
        var identity = Identity(occurrence.InstanceId, occurrence.Frozen, occurrence.SubjectId, reason);
        var accepted = OperationRows.Accept(connection, transaction, identity, now);
        if (!accepted.IsNew)
        {
            if (Read(connection, transaction, occurrence.InstanceId, occurrence.Frozen, occurrence.SubjectId) != reason)
                throw ScheduleMinuteCodec.Invalid();
            return;
        }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.schedule_occurrence_outcomes(occurrence_id,operation_id,reason_code) VALUES($occurrence,$operation,$reason);";
        command.Parameters.AddWithValue("$occurrence", occurrence.Frozen.OccurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$reason", reason); command.ExecuteNonQuery();
        command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$operation;"; command.ExecuteNonQuery();
    }

    internal static void VerifyInventory(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM main.operations o LEFT JOIN main.schedule_occurrence_outcomes b ON b.operation_id=o.operation_id WHERE o.operation_kind='schedule.occurrence.block' AND b.operation_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw ScheduleMinuteCodec.Invalid();
    }
}
