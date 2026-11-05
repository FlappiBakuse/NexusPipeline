using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed record DatabaseFileEvidence(string Suffix, long SizeBytes, string Sha256);
public sealed record DatabaseWalEvidence(uint PageSize, long CurrentGenerationFrames, long CurrentGenerationCommits,
    long InvalidatedTailFrames, long InvalidatedTailGenerations);
public sealed record DatabaseInspection(Guid InstanceId, string SchemaDigest, string NativeVersion,
    string DiagnosticCopyRoot, IReadOnlyList<DatabaseFileEvidence> SourceFiles, DatabaseWalEvidence? Wal = null)
{
    internal DatabaseCopy? OwnedCopy { get; init; }
}

public static class DatabaseInspector
{
    public static DatabaseInspection Inspect(string databasePath, Guid instanceId, string diagnosticRoot)
    {
        var database = Path.GetFullPath(databasePath);
        var destination = Path.GetFullPath(diagnosticRoot);
        var sourceDirectory = Path.GetDirectoryName(database)!;
        if (destination == sourceDirectory || Path.GetRelativePath(sourceDirectory, destination) is var relative &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative != ".." && !Path.IsPathRooted(relative))
            throw new IOException("Diagnostic copy must be outside database input directory");
        DatabaseConnections.RejectLinks(database);
        DatabaseConnections.RejectLinks(destination);
        var sources = new List<(string Suffix, FileStream Stream)>();
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                if (suffix.Length > 0 && !File.Exists(database + suffix)) continue;
                DatabaseConnections.RejectLinks(database + suffix);
                sources.Add((suffix, new FileStream(database + suffix, FileMode.Open, FileAccess.Read, FileShare.Read)));
            }
            DatabaseWalEvidence? walEvidence = null;
            if (sources.SingleOrDefault(item => item.Suffix == "-wal").Stream is { } wal)
                walEvidence = WalEvidence.Verify(sources.Single(item => item.Suffix.Length == 0).Stream, wal);
            if (sources.SingleOrDefault(item => item.Suffix == "-journal").Stream is { Length: > 0 })
                throw new IOException("database_rollback_journal_requires_readonly_diagnosis");
            Directory.CreateDirectory(destination);
            var copyRoot = Path.Combine(destination, Guid.NewGuid().ToString("N"));
            ExclusiveDirectory.Create(copyRoot);
            var identity = PhysicalObjectIdentity.Read(copyRoot);
            var evidence = CopyFrozenGroup(sources, copyRoot);
            // SQLite readers can update WAL shared memory; inspect only the isolated file group.
            string version, digest;
            using (var connection = DatabaseConnections.Open(Path.Combine(copyRoot, "nexus.db")))
            {
                digest = DatabaseSchema.VerifyCatalog(connection);
                VerifyIdentity(connection, instanceId, digest);
                VerifyIntegrity(connection);
                OperationReadIndexes.Ensure(connection);
                version = Convert.ToString(DatabaseConnections.Scalar(connection, "SELECT sqlite_version();"), CultureInfo.InvariantCulture)!;
            }
            return new(instanceId, digest, version, copyRoot, evidence, walEvidence)
                { OwnedCopy = DatabaseCopy.Capture(copyRoot, identity) };
        }
        catch (SqliteException exception)
        {
            throw new IOException("unrecognized_or_corrupt_database", exception);
        }
        finally
        {
            foreach (var (_, stream) in sources) stream.Dispose();
        }
    }

    private static IReadOnlyList<DatabaseFileEvidence> CopyFrozenGroup(IReadOnlyList<(string Suffix, FileStream Stream)> sources, string destination)
    {
        var evidence = new List<DatabaseFileEvidence>();
        foreach (var (suffix, source) in sources)
        {
            var length = source.Length;
            source.Position = 0;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0;
            using (var target = new FileStream(Path.Combine(destination, "nexus.db" + suffix), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536];
                int count;
                while ((count = source.Read(buffer)) != 0)
                {
                    target.Write(buffer.AsSpan(0, count));
                    digest.AppendData(buffer.AsSpan(0, count)); copied += count;
                }
                target.Flush(flushToDisk: true);
            }
            var sha = Convert.ToHexStringLower(digest.GetHashAndReset());
            if (source.Length != length || copied != length) throw new IOException("Database file group changed while copying");
            evidence.Add(new(suffix, length, sha));
        }
        return evidence;
    }

    internal static void VerifyIdentity(SqliteConnection connection, Guid expectedInstance, string? expectedDigest = null,
        SqliteTransaction? transaction = null)
    {
        long Scalar(string sql)
        {
            using var query = connection.CreateCommand(); query.Transaction = transaction; query.CommandText = sql;
            return Convert.ToInt64(query.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        if (Scalar("PRAGMA application_id;") != 1314411313 || Scalar("PRAGMA user_version;") != 1)
            throw new IOException("unsupported_database_generation");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT format,schema_version,instance_id,contract_digest FROM schema_info WHERE singleton=1;";
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != "nexus.state.database" || row.GetInt64(1) != 1 ||
            !Guid.TryParse(row.GetString(2), out var instance) || instance != expectedInstance ||
            row.GetString(3) != (expectedDigest ?? DatabaseSchema.Digest) || row.Read())
            throw new IOException("unsupported_database_identity");
    }

    internal static void VerifyIntegrity(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var check = connection.CreateCommand(); check.Transaction = transaction; check.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(Convert.ToString(check.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.Ordinal))
            throw new IOException("database_integrity_failure");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        using var row = command.ExecuteReader();
        if (row.Read()) throw new IOException("database_foreign_key_failure");
    }
}
