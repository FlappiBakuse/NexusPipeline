using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class DatabaseReadIndexes
{
    private static readonly IReadOnlyDictionary<string, (string Table, string Sql)> Definitions =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["operations_parent_read"] = ("operations", "CREATE INDEX operations_parent_read ON operations(initial_journal_id,operation_kind)"),
        ["operations_kind_read"] = ("operations", "CREATE INDEX operations_kind_read ON operations(operation_kind,initial_journal_id)"),
        ["operations_accepted_read"] = ("operations",
            "CREATE INDEX operations_accepted_read ON operations(status) WHERE status='accepted'"),
        ["execution_start_retention_read"] = ("operations",
            "CREATE INDEX execution_start_retention_read ON operations(julianday(created_at),operation_id) WHERE operation_kind='execution.start'"),
        ["account_run_records_ended_read"] = ("account_run_records",
            "CREATE INDEX account_run_records_ended_read ON account_run_records(ended_at_utc_ticks,run_id,user_id)"),
        ["recovery_capacity_open_read"] = ("recovery_capacity_reservations",
            "CREATE INDEX recovery_capacity_open_read ON recovery_capacity_reservations(owner_journal_id) WHERE released=0"),
        ["journals_unfinished_read"] = ("operation_journals",
            "CREATE INDEX journals_unfinished_read ON operation_journals(journal_id) WHERE phase<>'committed'"),
        ["file_effects_pending_read"] = ("file_effects",
            "CREATE INDEX file_effects_pending_read ON file_effects(journal_id) WHERE state='pending'")
    };

    internal static bool Recognize(string type, string name, string table, string? sql)
    {
        if (!Definitions.TryGetValue(name, out var expected)) return false;
        if (type != "index" || table != expected.Table || sql != expected.Sql)
            throw new IOException("unsupported_database_catalog");
        return true;
    }

    internal static void Ensure(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        foreach (var definition in Definitions.Values)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = definition.Sql.Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
            command.ExecuteNonQuery();
        }
    }
}
