using System.Globalization;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Records;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class RecordSummaryReader(StateDatabase database)
{
    public IReadOnlyList<BindingTaskSummary> ReadBindings(IReadOnlyList<NexusPipeline.Core.Automation.AutomationBinding> bindings)
    {
        if (bindings.Count > 10000 || bindings.Select(item => item.Id).Distinct().Count() != bindings.Count)
            throw new ArgumentException("binding_summary_query_invalid");
        if (bindings.Count == 0) return [];
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "WITH requested AS (SELECT json_extract(value,'$.binding') binding_id," +
            "json_extract(value,'$.user') user_id,json_extract(value,'$.script') script_id FROM json_each($bindings))," +
            "ranked AS (SELECT q.binding_id,b.user_id,b.script_id,b.run_id,b.tone,b.reason,r.ended_at_utc_ticks," +
            "r.history_retired_at_utc_ticks,ROW_NUMBER() OVER (PARTITION BY q.binding_id ORDER BY r.ended_at_utc_ticks DESC,r.run_id DESC) rank " +
            "FROM requested q JOIN binding_task_records b ON b.binding_id=q.binding_id AND b.user_id=q.user_id AND b.script_id=q.script_id JOIN run_records r ON r.run_id=b.run_id) " +
            "SELECT binding_id,user_id,script_id,run_id,tone,reason,ended_at_utc_ticks,history_retired_at_utc_ticks FROM ranked WHERE rank=1;";
        command.Parameters.AddWithValue("$bindings", System.Text.Json.JsonSerializer.Serialize(bindings.Select(item =>
            new { binding = item.Id.ToString("D"), user = item.UserId.ToString("D"), script = item.ScriptId.ToString("D") })));
        var result = new List<BindingTaskSummary>();
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var retired = !rows.IsDBNull(7);
            result.Add(new(Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), Guid.Parse(rows.GetString(2)),
                retired ? null : Guid.Parse(rows.GetString(3)), new DateTimeOffset(rows.GetInt64(6), TimeSpan.Zero),
                retired ? "muted" : rows.GetString(4), retired ? "record_deleted" : rows.GetString(5)));
        }
        return result.AsReadOnly();
    }

    private const string Measures = "COUNT(*),SUM(business_outcome=1),SUM(business_outcome=3),SUM(business_outcome=5)," +
        "SUM(business_outcome=0),SUM(duration_kind=0),COALESCE(SUM(CASE WHEN duration_kind=0 THEN duration_milliseconds ELSE 0 END),0)," +
        "SUM(duration_kind=1),SUM(duration_kind=2)";

    public CompletedRunRecord? Read(Guid runId, Guid? userId = null)
    {
        if (runId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("record_run_invalid");
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,automation_id,user_id,ended_at_utc_ticks,duration_milliseconds,attempt_id,attempt_ordinal," +
            "engine_outcome,business_outcome,lifecycle_outcome,recovery_outcome,reason_code,duration_kind,end_time_kind " +
            "FROM " + (userId.HasValue ? "account_history" : "run_records") +
            " WHERE run_id=$run AND history_retired_at_utc_ticks IS NULL" + (userId.HasValue ? " AND user_id=$user" : "") + ";";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        if (userId.HasValue) command.Parameters.AddWithValue("$user", userId.Value.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? RunRecordStore.ReadRecord(reader) : null;
    }

    public RecordSummary Read(RecordSummaryQuery query)
    {
        query.Validate();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var where = Prepare(command, query);
        var days = ReadDays(command, query, where);
        var users = ReadUsers(command, query, where);
        transaction.Commit();
        return new(days.Totals, days.Days, users.Users, users.NextUserId);
    }

    public RecordDaySummary ReadDays(RecordSummaryQuery query)
    {
        query.Validate();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        return ReadDays(command, query, Prepare(command, query));
    }

    public RecordUserPage ReadUsers(RecordSummaryQuery query)
    {
        query.Validate();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        return ReadUsers(command, query, Prepare(command, query));
    }

    private static string Prepare(SqliteCommand command, RecordSummaryQuery query)
    {
        var where = "history_retired_at_utc_ticks IS NULL AND ended_at_utc_ticks >= $from AND ended_at_utc_ticks < $until";
        command.Parameters.AddWithValue("$from", query.From.UtcTicks);
        command.Parameters.AddWithValue("$until", query.Until.UtcTicks);
        if (query.UserId is { } user) { where += " AND user_id=$user"; command.Parameters.AddWithValue("$user", user.ToString("D")); }
        if (query.Business is { } business) { where += " AND business_outcome=$business"; command.Parameters.AddWithValue("$business", (int)business); }
        if (query.Lifecycle is { } lifecycle) { where += " AND lifecycle_outcome=$lifecycle"; command.Parameters.AddWithValue("$lifecycle", (int)lifecycle); }
        return where;
    }

    private static RecordDaySummary ReadDays(SqliteCommand command, RecordSummaryQuery query, string where)
    {
        // Integer day keys retain the caller's selected offset without relying on SQLite's local timezone.
        command.Parameters.AddWithValue("$offset", query.OffsetMinutes * TimeSpan.TicksPerMinute);
        command.Parameters.AddWithValue("$day", TimeSpan.TicksPerDay);
        command.CommandText = "SELECT (ended_at_utc_ticks+$offset)/$day AS day," + Measures +
            " FROM account_history WHERE " + where + " GROUP BY day ORDER BY day DESC;";
        var days = new List<RecordDay>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) days.Add(new(new DateTime(reader.GetInt64(0) * TimeSpan.TicksPerDay, DateTimeKind.Utc)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ReadTotals(reader, 1)));
        var totals = new RecordTotals(days.Sum(day => day.Totals.Runs), days.Sum(day => day.Totals.Succeeded),
            days.Sum(day => day.Totals.Failed), days.Sum(day => day.Totals.Cancelled), days.Sum(day => day.Totals.Unverified),
            days.Sum(day => day.Totals.MeasuredCount), days.Sum(day => day.Totals.MeasuredMilliseconds),
            days.Sum(day => day.Totals.LowerBoundCount), days.Sum(day => day.Totals.UnknownDurationCount));
        return new(totals, days.AsReadOnly());
    }

    private static RecordUserPage ReadUsers(SqliteCommand command, RecordSummaryQuery query, string where)
    {
        var userWhere = where;
        if (query.AfterUserId is { } after)
        { userWhere += " AND user_id>$after"; command.Parameters.AddWithValue("$after", after.ToString("D")); }
        command.CommandText = "SELECT user_id," + Measures + " FROM account_history WHERE " + userWhere +
            " GROUP BY user_id ORDER BY user_id LIMIT 201;";
        var users = new List<RecordUser>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) users.Add(new(reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)), ReadTotals(reader, 1)));
        var more = users.Count > 200;
        if (more) users.RemoveAt(200);
        return new(users.AsReadOnly(), more ? users[^1].UserId : null);
    }

    private static RecordTotals ReadTotals(SqliteDataReader reader, int start)
    {
        long Value(int index) => reader.IsDBNull(start + index) ? 0 : reader.GetInt64(start + index);
        return new(Value(0), Value(1), Value(2), Value(3), Value(4), Value(5), Value(6), Value(7), Value(8));
    }
}
