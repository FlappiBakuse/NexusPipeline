using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed record DatabaseFileEvidence(string Suffix, long SizeBytes, string Sha256)
{
    public string Name { get; init; } = "runtime.db" + Suffix;
}
public sealed record DatabaseDomainInspection(string Name, string SchemaDigest, long SizeBytes);
public sealed record DatabaseInspection(Guid InstanceId, string SchemaDigest, string NativeVersion,
    string DiagnosticCopyRoot, IReadOnlyList<DatabaseFileEvidence> SourceFiles)
{
    public IReadOnlyList<DatabaseDomainInspection> Databases { get; init; } = [];
    internal DatabaseCopy? OwnedCopy { get; init; }
}

public static class DatabaseInspector
{
    public static DatabaseInspection Inspect(string databasePath, Guid instanceId, string diagnosticRoot)
    {
        using var timing = StartupProfile.Measure("database.inspect");
        var database = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(databasePath));
        var sourceRoot = Path.GetDirectoryName(database)!;
        var destination = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(diagnosticRoot));
        var relative = Path.GetRelativePath(sourceRoot, destination);
        if (destination == sourceRoot || !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            relative != ".." && !Path.IsPathRooted(relative))
            throw new IOException("Diagnostic copy must be outside database input directory");
        var names = DatabaseJournals.Inventory(database);
        DatabaseJournals.VerifyPaths(database);
        var sources = new List<(string Name, FileStream Stream)>();
        try
        {
            foreach (var name in names)
                sources.Add((name, new FileStream(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(sourceRoot, name)),
                    FileMode.Open, FileAccess.Read, FileShare.Read)));
            Directory.CreateDirectory(destination);
            var copyRoot = Path.Combine(destination, Guid.NewGuid().ToString("N"));
            ExclusiveDirectory.Create(copyRoot);
            var identity = PhysicalObjectIdentity.Read(copyRoot);
            var evidence = CopyFrozenGroup(sources, copyRoot);
            DatabaseJournals.RebindCopy(sourceRoot, copyRoot);
            string version;
            IReadOnlyList<DatabaseDomainInspection> domains;
            using (var connection = DatabaseConnections.Open(Path.Combine(copyRoot, "runtime.db")))
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                DatabaseSchema.VerifyCatalog(connection, transaction);
                VerifyIdentity(connection, instanceId, transaction: transaction);
                VerifyIntegrity(connection, transaction);
                DatabaseReadIndexes.Ensure(connection, transaction);
                domains = DatabaseLayout.Domains.Select(domain => new DatabaseDomainInspection(DatabaseLayout.Name(domain),
                    DatabaseSchema.DomainDigest(domain), new FileInfo(DatabaseLayout.PathFor(database, domain)).Length)).ToArray();
                transaction.Commit();
                version = Convert.ToString(DatabaseConnections.Scalar(connection, "SELECT sqlite_version();"), CultureInfo.InvariantCulture)!;
            }
            return new(instanceId, DatabaseSchema.Digest, version, copyRoot, evidence)
                { Databases = domains, OwnedCopy = DatabaseCopy.Capture(copyRoot, identity) };
        }
        catch (SqliteException error) { throw new IOException("unrecognized_or_corrupt_database", error); }
        finally { foreach (var (_, input) in sources) input.Dispose(); }
    }

    private static IReadOnlyList<DatabaseFileEvidence> CopyFrozenGroup(IReadOnlyList<(string Name, FileStream Stream)> sources, string destination)
    {
        using var timing = StartupProfile.Measure("database.copy");
        var evidence = new List<DatabaseFileEvidence>();
        foreach (var (name, input) in sources)
        {
            var length = input.Length;
            input.Position = 0;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0;
            using (var target = new FileStream(Path.Combine(destination, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer)) != 0)
                { target.Write(buffer.AsSpan(0, count)); digest.AppendData(buffer.AsSpan(0, count)); copied += count; }
                target.Flush(flushToDisk: true);
            }
            if (input.Length != length || copied != length) throw new IOException("database_group_changed_while_copying");
            evidence.Add(new(name.EndsWith("-journal", StringComparison.Ordinal) ? "-journal" : "", length,
                Convert.ToHexStringLower(digest.GetHashAndReset())) { Name = name });
        }
        return evidence.AsReadOnly();
    }

    internal static void RecheckSource(string coordinator, IReadOnlyList<DatabaseFileEvidence> evidence)
    {
        var names = DatabaseJournals.Inventory(coordinator);
        if (!names.SequenceEqual(evidence.Select(item => item.Name).Order(StringComparer.Ordinal)))
            throw new IOException("database_group_changed_after_inspection");
        foreach (var expected in evidence)
        {
            using var input = File.OpenRead(Path.Combine(Path.GetDirectoryName(coordinator)!, expected.Name));
            if (input.Length != expected.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(input)) != expected.Sha256)
                throw new IOException("database_group_changed_after_inspection");
        }
    }

    internal static void VerifyIdentity(SqliteConnection connection, Guid instanceId, string? expectedDigest = null,
        SqliteTransaction? transaction = null)
    {
        if (expectedDigest is not null && expectedDigest != DatabaseSchema.Digest) throw new IOException("unsupported_database_identity");
        foreach (var domain in DatabaseLayout.Domains.Where(domain => (DatabaseConnections.Domains(connection) & domain) != 0))
            VerifyDomainIdentity(connection, instanceId, domain, transaction);
    }

    internal static void VerifyDomainIdentity(SqliteConnection connection, Guid instanceId, DatabaseDomain domain,
        SqliteTransaction? transaction = null, bool standalone = false)
    {
        var alias = standalone ? "main" : DatabaseLayout.Alias(domain);
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = $"PRAGMA {alias}.application_id;";
        if (Convert.ToInt64(query.ExecuteScalar(), CultureInfo.InvariantCulture) != 1314411313) throw new IOException("unsupported_database_generation");
        query.CommandText = $"PRAGMA {alias}.user_version;";
        if (Convert.ToInt64(query.ExecuteScalar(), CultureInfo.InvariantCulture) != 2) throw new IOException("unsupported_database_generation");
        query.CommandText = $"SELECT format,schema_version,instance_id,domain_name,contract_digest FROM {alias}.schema_info WHERE singleton=1;";
        using var row = query.ExecuteReader();
        if (!row.Read() || row.GetString(0) != "nexus.state.domain" || row.GetInt64(1) != 2 ||
            !Guid.TryParse(row.GetString(2), out var actual) || actual != instanceId || row.GetString(3) != DatabaseLayout.Name(domain) ||
            row.GetString(4) != DatabaseSchema.DomainDigest(domain) || row.Read()) throw new IOException("unsupported_database_identity");
    }

    internal static void VerifyIntegrity(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var timing = StartupProfile.Measure("database.integrity");
        foreach (var domain in DatabaseLayout.Domains.Where(domain => (DatabaseConnections.Domains(connection) & domain) != 0))
        {
            var alias = DatabaseLayout.Alias(domain);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"PRAGMA {alias}.quick_check;";
            if (!string.Equals(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.Ordinal))
                throw new IOException("database_integrity_failure");
            command.CommandText = $"PRAGMA {alias}.foreign_key_check;";
            using var rows = command.ExecuteReader();
            if (rows.Read()) throw new IOException("database_foreign_key_failure");
        }
        if (DatabaseConnections.Domains(connection) == DatabaseDomain.All) DatabaseRelations.VerifyAll(connection, transaction);
    }
}
