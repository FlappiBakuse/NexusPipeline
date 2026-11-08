using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class DatabaseReadIndexes
{
    private static readonly IReadOnlyDictionary<string, (string Table, string Sql)> Definitions =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["operations_parent_read"] = ("operations", "CREATE INDEX operations_parent_read ON operations(initial_journal_id,operation_kind)"),
        ["operations_kind_read"] = ("operations", "CREATE INDEX operations_kind_read ON operations(operation_kind,initial_journal_id)"),
        ["account_run_records_ended_read"] = ("account_run_records",
            "CREATE INDEX account_run_records_ended_read ON account_run_records(ended_at_utc_ticks,run_id,user_id)")
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
