using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class StateDatabaseTests
{
    [Fact(DisplayName = "An Actual Sqlite Busy Writer Cannot Publish Partial Acceptance Or Consume The Operation Identity")]
    public void RejectsBusyWriters()
    {
        var (_, _, database) = NewDatabase();
        var identity = new OperationIdentity(Guid.NewGuid(), "fixture", new string('c', 64), "fixture.command");
        using (var writer = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={database.DatabasePath};Pooling=False"))
        {
            writer.Open();
            using var transaction = writer.BeginTransaction(deferred: false);
            var elapsed = Stopwatch.StartNew();
            var error = Assert.Throws<SqliteException>(() => database.Accept(identity, DateTimeOffset.UtcNow));
            Assert.Equal(5, error.SqliteErrorCode);
            Assert.InRange(elapsed.Elapsed.TotalSeconds, 0, 10);
            transaction.Rollback();
        }
        Assert.True(database.Accept(identity, DateTimeOffset.UtcNow).IsNew);
        Assert.False(database.Accept(identity, DateTimeOffset.UtcNow).IsNew);
    }

    [Fact(DisplayName = "Fresh Databases Use Persistent Rollback Full And Foreign Keys And Cannot Overwrite Existing State")]
    public void UsesDurableDefaults()
    {
        var (_, instance, database) = NewDatabase();
        var settings = database.ReadSettings();
        Assert.Equal("persist", settings.JournalMode);
        Assert.Equal(2, settings.Synchronous);
        Assert.Equal(1, settings.ForeignKeys);
        Assert.False(string.IsNullOrWhiteSpace(settings.NativeVersion));
        var before = Fingerprints(Path.GetDirectoryName(database.DatabasePath)!);
        Assert.Throws<IOException>(() => StateDatabase.CreateFresh(instance));
        Assert.Equal(before, Fingerprints(Path.GetDirectoryName(database.DatabasePath)!));
    }

    [Fact(DisplayName = "Concurrent Repeated Operation Has One Acceptance And Cross Subject Cannot Inspect It")]
    public async Task SerializesOperationIdentity()
    {
        var (_, _, database) = NewDatabase();
        var identity = new OperationIdentity(Guid.NewGuid(), "subject-a", new string('a', 64), "fixture.command");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => database.Accept(identity, DateTimeOffset.UtcNow))));
        Assert.Single(results, result => result.IsNew);
        Assert.All(results, result => Assert.Equal(identity.OperationId, result.OperationId));
        Assert.Throws<OperationConflictException>(() => database.Accept(new(identity.OperationId, "subject-b", identity.RequestDigest, identity.Kind), DateTimeOffset.UtcNow));
        Assert.Throws<OperationConflictException>(() => database.Accept(new(identity.OperationId, identity.SubjectId, new string('b', 64), identity.Kind), DateTimeOffset.UtcNow));
        Assert.Throws<OperationConflictException>(() => database.Accept(new(identity.OperationId, identity.SubjectId, identity.RequestDigest, "other.command"), DateTimeOffset.UtcNow));
        Assert.False(database.Accept(identity, DateTimeOffset.UtcNow).IsNew);
    }

    [Fact(DisplayName = "Inspection Uses An Isolated Copy And Does Not Change The Source File Group")]
    public void InspectsIsolatedCopies()
    {
        var (root, instance, database) = NewDatabase();
        var before = Fingerprints(Path.GetDirectoryName(database.DatabasePath)!);
        var result = DatabaseInspector.Inspect(database.DatabasePath, instance.InstanceId, Path.Combine(root, "diagnostics"));
        Assert.Equal(instance.InstanceId, result.InstanceId);
        Assert.Equal(StateDatabase.SchemaDigest, result.SchemaDigest);
        Assert.NotEmpty(result.SourceFiles);
        Assert.Equal(before, Fingerprints(Path.GetDirectoryName(database.DatabasePath)!));
        Assert.NotEqual(Path.GetDirectoryName(database.DatabasePath), result.DiagnosticCopyRoot);
    }

    [Theory(DisplayName = "Unknown Database Is Preserved Instead Of Being Rebuilt")]
    [InlineData("generation")]
    [InlineData("catalog")]
    [InlineData("identity")]
    [InlineData("corrupt")]
    public void PreservesUnknownDatabase(string fault)
    {
        var (root, instance, database) = NewDatabase();
        if (fault == "corrupt") File.WriteAllBytes(database.DatabasePath, [1, 2, 3, 4]);
        else
        {
            using var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={database.DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = fault switch
            {
                "generation" => "PRAGMA user_version=17;",
                "catalog" => "CREATE TABLE unexpected(value TEXT);",
                "identity" => "UPDATE main.schema_info SET instance_id='00000000-0000-4000-8000-000000000001';",
                _ => throw new InvalidOperationException()
            };
            command.ExecuteNonQuery();
        }
        var before = Fingerprints(Path.GetDirectoryName(database.DatabasePath)!);
        Assert.Throws<IOException>(() => DatabaseInspector.Inspect(database.DatabasePath, instance.InstanceId, Path.Combine(root, "diagnostics")));
        Assert.Equal(before, Fingerprints(Path.GetDirectoryName(database.DatabasePath)!));
    }

    [Fact(DisplayName = "Active Database Writer Prevents Inspection Before Any Diagnostic Copy Is Created")]
    public void RejectsActiveWriters()
    {
        var (root, instance, database) = NewDatabase();
        using var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={database.DatabasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM main.schema_info;";
        Assert.Equal(1L, command.ExecuteScalar());
        var diagnostics = Path.Combine(root, "diagnostics");
        Assert.Throws<IOException>(() => DatabaseInspector.Inspect(database.DatabasePath, instance.InstanceId, diagnostics));
        Assert.False(Directory.Exists(diagnostics));
    }

    [Fact(DisplayName = "Sqlite Backup Keeps The Committed Snapshot Independent Of Later Writes")]
    public void KeepsBackupSnapshot()
    {
        var (root, instance, database) = NewDatabase();
        var original = Guid.NewGuid();
        database.Accept(new(original, "subject", new string('a', 64), "fixture.command"), DateTimeOffset.UtcNow);
        var backupDirectory = Path.Combine(root, "backup-files");
        Directory.CreateDirectory(backupDirectory);
        var path = Path.Combine(backupDirectory, "runtime.db");
        var backup = database.BackupTo(path);
        Assert.Equal(instance.InstanceId, backup.InstanceId);
        Assert.True(backup.SizeBytes > 0);
        database.Accept(new(Guid.NewGuid(), "subject", new string('b', 64), "fixture.command"), DateTimeOffset.UtcNow);
        using (var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={path};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT operation_id FROM main.operations;";
            using var reader = query.ExecuteReader();
            Assert.True(reader.Read()); Assert.Equal(original, Guid.Parse(reader.GetString(0)));
            Assert.False(reader.Read());
        }
        var inspected = DatabaseInspector.Inspect(path, instance.InstanceId, Path.Combine(root, "restored-inspection"));
        Assert.Equal(StateDatabase.SchemaDigest, inspected.SchemaDigest);
        using var file = File.OpenRead(path);
        Assert.Equal(backup.Sha256, Convert.ToHexStringLower(SHA256.HashData(file)));
        Assert.Throws<IOException>(() => database.BackupTo(path));
    }

    [Theory(DisplayName = "Database Creation Rechecks Runtime Identity Before Writing State")]
    [InlineData("forged")]
    [InlineData("unknown")]
    [InlineData("old-format")]
    public void RechecksRuntimeIdentity(string fault)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var runtime = fault == "forged"
            ? new InstanceRoot(new ContractReader()).OpenOrCreate(root)
            : new RuntimeInstance(Guid.NewGuid(), root, DateTimeOffset.UtcNow);
        if (fault == "forged") runtime = runtime with { InstanceId = Guid.NewGuid() };
        if (fault == "unknown") File.WriteAllText(Path.Combine(root, "legacy.json"), "preserve these bytes");
        if (fault == "old-format") File.WriteAllText(Path.Combine(root, "instance.json"), "{\"format\":\"nexus.instance\",\"version\":17}");
        var before = Fingerprints(root);
        if (fault == "old-format") Assert.Throws<JsonException>(() => StateDatabase.CreateFresh(runtime));
        else Assert.ThrowsAny<IOException>(() => StateDatabase.CreateFresh(runtime));
        Assert.False(Directory.Exists(Path.Combine(root, "state")));
        Assert.Equal(before, Fingerprints(root));
    }

    [Theory(DisplayName = "Database Initialization Cannot Rebuild Lost State Or Ignore Existing Runtime Data")]
    [InlineData("lost-state")]
    [InlineData("unknown-data")]
    public void RejectsLostInitialization(string fault)
    {
        RuntimeInstance instance;
        if (fault == "lost-state")
        {
            var (_, created, database) = NewDatabase();
            instance = created;
            var state = Path.GetDirectoryName(database.DatabasePath)!;
            foreach (var path in Directory.GetFiles(state)) File.Delete(path);
            Directory.Delete(state);
        }
        else
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
            instance = new InstanceRoot(new ContractReader()).OpenOrCreate(root);
            File.WriteAllText(Path.Combine(root, "user-data.txt"), "preserve user data");
        }
        var before = Fingerprints(instance.RuntimeRoot);
        Assert.Throws<IOException>(() => StateDatabase.CreateFresh(instance));
        Assert.False(Directory.Exists(Path.Combine(instance.RuntimeRoot, "state")));
        Assert.Equal(before, Fingerprints(instance.RuntimeRoot));
    }

    private static (string Root, RuntimeInstance Instance, StateDatabase Database) NewDatabase()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Explicit test root required"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var instance = new InstanceRoot(new ContractReader()).OpenOrCreate(Path.Combine(root, "runtime"));
        return (root, instance, StateDatabase.CreateFresh(instance));
    }

    private static string[] Fingerprints(string directory) => Directory.GetFiles(directory).Order().Select(path =>
    {
        using var stream = File.OpenRead(path);
        return Path.GetFileName(path) + ":" + Convert.ToHexStringLower(SHA256.HashData(stream));
    }).ToArray();
}
