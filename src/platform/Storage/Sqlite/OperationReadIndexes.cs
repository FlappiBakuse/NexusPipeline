using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class OperationReadIndexes
{
    private static readonly IReadOnlyDictionary<string, string> Definitions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["operations_parent_read"] = "CREATE INDEX operations_parent_read ON operations(initial_journal_id,operation_kind)",
        ["operations_kind_read"] = "CREATE INDEX operations_kind_read ON operations(operation_kind,initial_journal_id)"
    };

    internal static bool Recognize(string type, string name, string table, string? sql)
    {
        if (!Definitions.TryGetValue(name, out var expected)) return false;
        if (type != "index" || table != "operations" || sql != expected)
            throw new IOException("unsupported_database_catalog");
        return true;
    }

    internal static void Ensure(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        foreach (var definition in Definitions.Values)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = definition.Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
            command.ExecuteNonQuery();
        }
    }
}
