using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class DatabaseSchema
{
    internal static string Sql { get; } = ReadSql() + ExtensionDataSchema.Sql + ExtensionImageSchema.Sql +
        SecretDocumentSchema.Sql + ManualWorkSchema.Sql + WorkEffectSchema.Sql + EffectRotationSchema.Sql +
        NotificationManualSchema.Sql + EffectEpochSchema.Sql + TargetInvocationSchema.Sql;
    internal static string Digest { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql)));

    internal static string VerifyCatalog(SqliteConnection actual, SqliteTransaction? transaction = null)
    {
        var catalog = ReadCatalog(actual, transaction);
        using var expected = new SqliteConnection("Data Source=:memory:;Mode=Memory;Pooling=False");
        expected.Open();
        using var create = expected.CreateCommand();
        create.CommandText = Sql;
        create.ExecuteNonQuery();
        if (ReadCatalog(expected).SequenceEqual(catalog)) return Digest;
        throw new IOException("unsupported_database_catalog");
    }

    private static IReadOnlyList<string> ReadCatalog(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY type,name;";
        using var rows = command.ExecuteReader();
        var result = new List<string>();
        while (rows.Read())
        {
            if (result.Count >= 1024) throw new IOException("database_catalog_limit");
            // Exact nonunique read indexes do not change the logical state contract.
            if (OperationReadIndexes.Recognize(rows.GetString(0), rows.GetString(1), rows.GetString(2),
                    rows.IsDBNull(3) ? null : rows.GetString(3))) continue;
            result.Add(JsonSerializer.Serialize(Enumerable.Range(0, 4).Select(index => rows.IsDBNull(index) ? null : rows.GetString(index))));
        }
        return result;
    }

    private static string ReadSql()
    {
        using var stream = typeof(DatabaseSchema).Assembly.GetManifestResourceStream("Storage.StateSchema.sql")
            ?? throw new InvalidOperationException("Missing state schema");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }
}
