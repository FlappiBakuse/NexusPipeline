using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal static class RuntimeFingerprint
{
    internal static string[] Capture(string root)
    {
        var database = Path.Combine(root, "state", "runtime.db");
        var result = new List<string>();
        using (var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            Cache = SqliteCacheMode.Private
        }.ToString()))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            foreach (var domain in DatabaseLayout.Domains)
            {
                var alias = DatabaseLayout.Alias(domain);
                var name = "state/" + DatabaseLayout.Name(domain) + ".db";
                CaptureDatabase(command, alias, name, result);
            }
            transaction.Commit();
        }
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path) == "instance.lock" || Path.GetDirectoryName(path) == Path.GetDirectoryName(database) &&
                DatabaseJournals.Known(Path.GetFileName(path))) continue;
            result.Add(Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static void CaptureDatabase(SqliteCommand command, string alias, string name, List<string> result)
    {
        foreach (var pragma in new[] { "application_id", "user_version" })
        {
            command.CommandText = "PRAGMA " + alias + "." + pragma;
            result.Add(name + ":" + pragma + ":" + command.ExecuteScalar());
        }
        command.CommandText = "SELECT type,name,tbl_name,sql FROM " + alias + ".sqlite_schema ORDER BY type,name;";
        var tables = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                result.Add(name + ":schema:" + Row(reader));
                if (reader.GetString(0) == "table") tables.Add(reader.GetString(1));
            }
        foreach (var table in tables)
        {
            command.CommandText = "SELECT * FROM " + alias + ".\"" + table.Replace("\"", "\"\"") + "\"";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read()) rows.Add(Row(reader));
            foreach (var row in rows.Order(StringComparer.Ordinal)) result.Add(name + ":" + table + ":" + row);
        }
    }

    private static string Row(SqliteDataReader reader)
    {
        var values = new object?[reader.FieldCount];
        for (var index = 0; index < values.Length; index++) values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        return JsonSerializer.Serialize(values);
    }
}
