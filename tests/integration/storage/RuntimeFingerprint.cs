using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.StorageTests;

internal static class RuntimeFingerprint
{
    internal static string[] Capture(string root)
    {
        var database = Path.Combine(root, "state", "nexus.db");
        var result = new List<string>();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            Cache = SqliteCacheMode.Private
        }.ToString()))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            foreach (var pragma in new[] { "application_id", "user_version" })
            {
                command.CommandText = "PRAGMA " + pragma;
                result.Add("state/nexus.db:" + pragma + ":" + command.ExecuteScalar());
            }
            command.CommandText = "SELECT type,name,tbl_name,rootpage,sql FROM sqlite_schema ORDER BY type,name;";
            var tables = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    result.Add("state/nexus.db:schema:" + Row(reader));
                    if (reader.GetString(0) == "table") tables.Add(reader.GetString(1));
                }
            foreach (var table in tables)
            {
                command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
                using var reader = command.ExecuteReader();
                var rows = new List<string>();
                while (reader.Read()) rows.Add(Row(reader));
                foreach (var row in rows.Order(StringComparer.Ordinal)) result.Add("state/nexus.db:" + table + ":" + row);
            }
            transaction.Commit();
        }
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path) == "instance.lock" || path == database ||
                path == database + "-wal" || path == database + "-shm") continue;
            result.Add(Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static string Row(SqliteDataReader reader)
    {
        var values = new object?[reader.FieldCount];
        for (var index = 0; index < values.Length; index++) values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        return JsonSerializer.Serialize(values);
    }
}
