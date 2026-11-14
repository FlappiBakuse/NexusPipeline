using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Records;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class RecordPageQuery
{
    private static readonly string[] Columns = ["run_id", "automation_id", "user_id", "ended_at_utc_ticks", "duration_milliseconds",
        "attempt_id", "attempt_ordinal", "engine_outcome", "business_outcome", "lifecycle_outcome", "recovery_outcome", "reason_code", "duration_kind", "end_time_kind"];

    internal static SqliteCommand Create(SqliteConnection connection, RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(query); query.Validate();
        var command = connection.CreateCommand();
        var filters = new List<(string Column, string Operator, string Parameter)>();
        Add(query.AutomationId?.ToString("D"), "automation_id", "=", "$automation");
        Add(query.UserId?.ToString("D"), "user_id", "=", "$user");
        Add(query.Business is { } business ? (int)business : null, "business_outcome", "=", "$business");
        Add(query.Lifecycle is { } lifecycle ? (int)lifecycle : null, "lifecycle_outcome", "=", "$lifecycle");
        Add(query.EndedFromInclusive?.UtcTicks, "ended_at_utc_ticks", ">=", "$from");
        Add(query.EndedUntilExclusive?.UtcTicks, "ended_at_utc_ticks", "<", "$until");
        if (query.After is { } after)
        {
            command.Parameters.AddWithValue("$ended", after.EndedAt.UtcTicks);
            command.Parameters.AddWithValue("$run", after.RunId.ToString("D"));
        }
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        var run = Select(false) + " FROM records.run_records r WHERE " + Where(false);
        // The compound query can merge two ordered indexes without materializing the summary view.
        command.CommandText = (query.UserId.HasValue ? Select(true) +
            " FROM records.account_run_records a JOIN records.run_records r ON a.run_id=r.run_id WHERE " + Where(true) +
            " UNION ALL " + run + " AND NOT EXISTS(SELECT 1 FROM records.account_run_records a WHERE a.run_id=r.run_id)" : run) +
            " ORDER BY ended_at_utc_ticks,run_id LIMIT $limit;";
        return command;

        void Add(object? value, string column, string operation, string parameter)
        {
            if (value is null) return;
            filters.Add((column, operation, parameter));
            command.Parameters.AddWithValue(parameter, value);
        }
        string Select(bool account) => "SELECT " + string.Join(",", Columns.Select(column => Field(column, account) + " AS " + column));
        string Where(bool account)
        {
            var clauses = new List<string> { "r.history_retired_at_utc_ticks IS NULL" };
            if (query.Scope == RecordScope.Groups) clauses.Add("r.user_id IS NULL");
            clauses.AddRange(filters.Select(filter => Field(filter.Column, account) + filter.Operator + filter.Parameter));
            if (query.After is not null)
                clauses.Add("(" + Field("ended_at_utc_ticks", account) + "," + Field("run_id", account) + ")>($ended,$run)");
            return string.Join(" AND ", clauses);
        }
    }

    private static string Field(string column, bool account) =>
        (account && column is "run_id" or "user_id" or "ended_at_utc_ticks" or "engine_outcome" or
            "business_outcome" or "lifecycle_outcome" or "reason_code" ? "a." : "r.") + column;
}
