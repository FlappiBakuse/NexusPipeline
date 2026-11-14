using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class RunRecordStore(StateDatabase database) : IRunRecordStore
{
    public void AppendCompleted(IReadOnlyList<CompletedRunRecord> records)
    {
        if (records.Count is < 1 or > 256) throw new ArgumentException("record_batch_limit");
        var accepted = records.ToArray();
        foreach (var record in accepted) record.Validate();
        database.Write((connection, transaction) =>
        {
            InsertInside(connection, transaction, accepted);
            return true;
        });
    }

    internal static void PublishInside(SqliteConnection connection, SqliteTransaction transaction, CompletedRunRecord record)
    {
        record.Validate();
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT run_id,automation_id,user_id,ended_at_utc_ticks,duration_milliseconds,attempt_id,attempt_ordinal," +
            "engine_outcome,business_outcome,lifecycle_outcome,recovery_outcome,reason_code,duration_kind,end_time_kind FROM records.run_records WHERE run_id=$run;";
        query.Parameters.AddWithValue("$run", record.FinalAttempt.RunId.ToString("D"));
        using (var row = query.ExecuteReader())
            if (row.Read())
            {
                RequireSame(row, record);
                return;
            }
        InsertInside(connection, transaction, [record]);
    }

    internal static void RequirePublishedInside(SqliteConnection connection, SqliteTransaction transaction, CompletedRunRecord expected)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT run_id,automation_id,user_id,ended_at_utc_ticks,duration_milliseconds,attempt_id,attempt_ordinal," +
            "engine_outcome,business_outcome,lifecycle_outcome,recovery_outcome,reason_code,duration_kind,end_time_kind FROM records.run_records WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", expected.FinalAttempt.RunId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new IOException("record_execution_projection_missing");
        RequireSame(row, expected);
    }

    private static void RequireSame(SqliteDataReader row, CompletedRunRecord expected)
    {
        if (ReadRecord(row) != expected) throw new IOException("record_execution_projection_conflict");
    }

    private static void InsertInside(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<CompletedRunRecord> accepted)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO records.run_records VALUES($run,$automation,$user,$ended,$duration,$attempt,$ordinal,$engine,$business,$lifecycle,$recovery,$reason,$durationKind,$endTimeKind,NULL);
            """;
        foreach (var name in new[] { "$run", "$automation", "$user", "$ended", "$duration", "$attempt", "$ordinal", "$engine", "$business", "$lifecycle", "$recovery", "$reason", "$durationKind", "$endTimeKind" })
            command.Parameters.Add(new SqliteParameter(name, null));
        foreach (var record in accepted)
        {
            var facts = record.FinalAttempt;
            command.Parameters["$run"].Value = facts.RunId.ToString("D");
            command.Parameters["$automation"].Value = record.AutomationId.ToString("D");
            command.Parameters["$user"].Value = record.UserId is Guid user ? user.ToString("D") : DBNull.Value;
            command.Parameters["$ended"].Value = record.EndedAt.UtcTicks;
            command.Parameters["$duration"].Value = (object?)record.DurationMilliseconds ?? DBNull.Value;
            command.Parameters["$durationKind"].Value = (int)record.DurationKind;
            command.Parameters["$endTimeKind"].Value = (int)record.EndTimeKind;
            command.Parameters["$attempt"].Value = facts.AttemptId.ToString("D");
            command.Parameters["$ordinal"].Value = facts.Ordinal;
            command.Parameters["$engine"].Value = (int)facts.Engine;
            command.Parameters["$business"].Value = (int)facts.Business;
            command.Parameters["$lifecycle"].Value = (int)facts.Lifecycle;
            command.Parameters["$recovery"].Value = (int)facts.Recovery;
            command.Parameters["$reason"].Value = facts.ReasonCode;
            command.ExecuteNonQuery();
        }
    }

    public RecordPage ReadPage(RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(query); query.Validate();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var command = RecordPageQuery.Create(connection, query);
        using var reader = command.ExecuteReader();
        var rows = new List<CompletedRunRecord>();
        while (reader.Read()) rows.Add(ReadRecord(reader));
        var hasNext = rows.Count > query.Limit;
        if (hasNext) rows.RemoveAt(rows.Count - 1);
        return new(rows.AsReadOnly(), hasNext ? new(rows[^1].EndedAt, rows[^1].FinalAttempt.RunId) : null);
    }

    public IReadOnlyList<string> ExplainPage(RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(query); query.Validate();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var command = RecordPageQuery.Create(connection, query);
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read()) details.Add(reader.GetString(3));
        return details.AsReadOnly();
    }

    internal static CompletedRunRecord ReadRecord(SqliteDataReader reader)
    {
        var facts = new AttemptFacts(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(5)), reader.GetInt32(6),
            (EngineOutcome)reader.GetInt32(7), (BusinessOutcome)reader.GetInt32(8), (LifecycleOutcome)reader.GetInt32(9),
            (RecoveryOutcome)reader.GetInt32(10), reader.GetString(11));
        var record = new CompletedRunRecord(Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero), reader.IsDBNull(4) ? null : reader.GetInt64(4), facts,
            (RecordDurationKind)reader.GetInt32(12), (EndTimeKind)reader.GetInt32(13));
        record.Validate();
        return record;
    }
}
