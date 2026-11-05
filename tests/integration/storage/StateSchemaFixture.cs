using Microsoft.Data.Sqlite;

namespace NexusPipeline.StorageTests;

internal static class StateSchemaFixture
{
    internal static void RevertTargetRoots(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var schema = LegacyStateSchema.EpochSql;
        var pc = schema.IndexOf("CREATE UNIQUE INDEX execution_pc_target_one_root", StringComparison.Ordinal);
        var android = schema.IndexOf("CREATE UNIQUE INDEX execution_android_command_one_root", StringComparison.Ordinal);
        command.CommandText = "DROP INDEX execution_target_roots_step; " + schema[pc..(schema.IndexOf(';', pc) + 1)] +
            schema[android..(schema.IndexOf(';', android) + 1)];
        command.ExecuteNonQuery();
    }

    internal static void RevertTables(SqliteConnection connection, SqliteTransaction transaction, string schema,
        IReadOnlyList<string>? tables = null)
    {
        RevertTargetRoots(connection, transaction);
        using var expected = new SqliteConnection("Data Source=:memory:;Pooling=False"); expected.Open();
        using var create = expected.CreateCommand(); create.CommandText = schema; create.ExecuteNonQuery();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        create.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var expectedTables = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = create.ExecuteReader()) while (reader.Read()) expectedTables.Add(reader.GetString(0));
        command.CommandText = create.CommandText;
        var extraTables = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) if (!expectedTables.Contains(reader.GetString(0))) extraTables.Add(reader.GetString(0));
        foreach (var table in extraTables)
        {
            command.CommandText = "DROP TABLE \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\";";
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA legacy_alter_table=ON;"; command.ExecuteNonQuery();
        foreach (var table in tables ?? ["automations", "users", "queues", "schedules", "settings_snapshots"])
        {
            create.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;";
            var names = new List<string>();
            using (var reader = create.ExecuteReader())
                while (reader.Read()) names.Add('"' + reader.GetString(0) + '"');
            var columns = string.Join(',', names);
            var start = schema.IndexOf($"CREATE TABLE {table} (", StringComparison.Ordinal);
            var end = schema.IndexOf(';', start);
            command.CommandText = $"ALTER TABLE \"{table}\" RENAME TO \"fixture_{table}\";" + schema[start..(end + 1)] +
                $" INSERT INTO \"{table}\"({columns}) SELECT {columns} FROM \"fixture_{table}\"; DROP TABLE \"fixture_{table}\";";
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA legacy_alter_table=OFF;"; command.ExecuteNonQuery();
    }
}
