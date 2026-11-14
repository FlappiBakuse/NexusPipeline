using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class DatabaseSchema
{
    private sealed record DomainSchema(string Sql, string Digest, IReadOnlyList<string> Catalog);
    private static readonly IReadOnlyDictionary<DatabaseDomain, DomainSchema> Schemas =
        DatabaseLayout.Domains.ToDictionary(domain => domain, Load);
    internal static string Digest { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', DatabaseLayout.Domains.Select(domain => DatabaseLayout.Name(domain) + ":" + Schemas[domain].Digest)))));
    internal static string DomainDigest(DatabaseDomain domain) => Schemas[domain].Digest;

    internal static string VerifyCatalog(SqliteConnection actual, SqliteTransaction? transaction = null)
    {
        foreach (var domain in DatabaseLayout.Domains.Where(domain => (DatabaseConnections.Domains(actual) & domain) != 0))
            VerifyDomain(actual, domain, transaction);
        return Digest;
    }

    internal static void VerifyDomain(SqliteConnection connection, DatabaseDomain domain, SqliteTransaction? transaction = null,
        bool standalone = false)
    {
        if (!Schemas[domain].Catalog.SequenceEqual(ReadCatalog(connection, standalone ? "main" : DatabaseLayout.Alias(domain), transaction)))
            throw new IOException("unsupported_database_catalog");
    }

    internal static void CreateInside(SqliteConnection connection, SqliteTransaction transaction, DatabaseDomain domain)
    {
        var alias = DatabaseLayout.Alias(domain);
        var sql = Regex.Replace(Schemas[domain].Sql, @"(?m)^CREATE (TABLE|(?:UNIQUE )?INDEX|TRIGGER|VIEW) ",
            match => match.Value + alias + ".");
        sql = Regex.Replace(sql, @"(?m)^INSERT INTO ", "INSERT INTO " + alias + ".");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = sql + $"\nPRAGMA {alias}.application_id=1314411313; PRAGMA {alias}.user_version=2;";
        command.ExecuteNonQuery();
    }

    private static DomainSchema Load(DatabaseDomain domain)
    {
        var name = DatabaseLayout.Name(domain);
        using var stream = typeof(DatabaseSchema).Assembly.GetManifestResourceStream("Storage." + name + ".sql")
            ?? throw new InvalidOperationException("Missing domain schema: " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var sql = reader.ReadToEnd();
        using var expected = new SqliteConnection("Data Source=:memory:;Mode=Memory;Pooling=False");
        expected.Open();
        using var create = expected.CreateCommand(); create.CommandText = sql; create.ExecuteNonQuery();
        return new(sql, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))), ReadCatalog(expected, "main"));
    }

    private static IReadOnlyList<string> ReadCatalog(SqliteConnection connection, string alias, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT type,name,tbl_name,sql FROM {alias}.sqlite_schema ORDER BY type,name;";
        using var rows = command.ExecuteReader();
        var result = new List<string>();
        while (rows.Read())
        {
            if (result.Count >= 1024) throw new IOException("database_catalog_limit");
            if (DatabaseReadIndexes.Recognize(rows.GetString(0), rows.GetString(1), rows.GetString(2),
                    rows.IsDBNull(3) ? null : rows.GetString(3))) continue;
            result.Add(JsonSerializer.Serialize(Enumerable.Range(0, 4).Select(index => rows.IsDBNull(index) ? null : rows.GetString(index))));
        }
        return result;
    }
}
