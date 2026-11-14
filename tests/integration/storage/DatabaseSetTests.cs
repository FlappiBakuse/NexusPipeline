using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class DatabaseSetTests
{
    [Fact(DisplayName = "Settings And Their Operation Persist In Their Respective Business Databases")]
    public void StoresDomainFacts()
    {
        var (_, instance, database) = Create();
        var store = new HostSettingsStore(database);
        var change = new HostSettingsChange(0, HostSettings.Default with { HistoryRetentionDays = 30 });
        var operation = HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change);
        store.Commit(operation, change, DateTimeOffset.UtcNow);
        foreach (var domain in DatabaseLayout.Domains)
        {
            var file = DatabaseLayout.PathFor(database.DatabasePath, domain);
            Assert.True(File.Exists(file));
            Assert.Equal(instance.InstanceId.ToString("D"), Scalar(file, "SELECT instance_id FROM schema_info;"));
            Assert.Equal(DatabaseLayout.Name(domain), Scalar(file, "SELECT domain_name FROM schema_info;"));
        }
        Assert.Equal(30L, Scalar(DatabaseLayout.PathFor(database.DatabasePath, DatabaseDomain.System),
            "SELECT retention_days FROM settings_snapshots WHERE revision=1;"));
        Assert.Equal(operation.OperationId.ToString("D"), Scalar(database.DatabasePath, "SELECT operation_id FROM operations;"));
    }

    [Fact(DisplayName = "An Exception Reverts All Business Databases In The Transaction")]
    public void RollsBackDatabaseSet()
    {
        var (_, _, database) = Create();
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.database");
        Assert.Throws<IOException>(() => database.Write<bool>((connection, transaction) =>
        {
            OperationRows.Accept(connection, transaction, operation, DateTimeOffset.UtcNow);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE system.startup_registration_current SET command_json=x'7b7d' WHERE singleton=1;";
            command.ExecuteNonQuery();
            throw new IOException("fixture_interrupted");
        }));
        Assert.Equal(0L, Scalar(database.DatabasePath, "SELECT count(*) FROM operations;"));
        Assert.Equal(DBNull.Value, Scalar(DatabaseLayout.PathFor(database.DatabasePath, DatabaseDomain.System),
            "SELECT command_json FROM startup_registration_current;"));
    }

    [Fact(DisplayName = "A Cross Database Child Cannot Commit Without Its Parent")]
    public void RejectsCrossDomainOrphans()
    {
        var (_, _, database) = Create();
        var operation = Guid.NewGuid();
        Assert.Throws<IOException>(() => database.Write((connection, transaction) =>
        {
            InsertDocument(connection, transaction, operation);
            return true;
        }));
        Assert.Equal(0L, Scalar(DatabaseLayout.PathFor(database.DatabasePath, DatabaseDomain.Extensions),
            "SELECT count(*) FROM extension_documents;"));
    }

    [Fact(DisplayName = "Cross Database Parents May Be Published Later In The Same Atomic Save")]
    public void CompletesDeferredParents()
    {
        var (_, _, database) = Create();
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.database");
        database.Write((connection, transaction) =>
        {
            InsertDocument(connection, transaction, operation.OperationId);
            OperationRows.Accept(connection, transaction, operation, DateTimeOffset.UtcNow);
            return true;
        });
        Assert.Equal(operation.OperationId.ToString("D"), Scalar(DatabaseLayout.PathFor(database.DatabasePath, DatabaseDomain.Extensions),
            "SELECT operation_id FROM extension_documents;"));
        Assert.False(database.Accept(operation, DateTimeOffset.UtcNow).IsNew);
    }

    [Fact(DisplayName = "History Reading Does Not Open An Unrelated Locked Business Database")]
    public void ReadsSelectedDomain()
    {
        var (_, _, database) = Create();
        using var unrelated = new FileStream(DatabaseLayout.PathFor(database.DatabasePath, DatabaseDomain.System),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Records);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM records.run_records;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Theory(DisplayName = "Missing Or Foreign Database Members Never Become An Empty Instance")]
    [InlineData("missing")]
    [InlineData("identity")]
    [InlineData("catalog")]
    [InlineData("initialization")]
    public void PreservesIncompleteSet(string fault)
    {
        var (root, instance, database) = Create();
        var file = DatabaseLayout.PathFor(database.DatabasePath, DatabaseDomain.System);
        if (fault == "missing") File.Move(file, Path.Combine(root, "system-preserved.db"));
        else if (fault == "initialization") File.WriteAllBytes(Path.Combine(instance.RuntimeRoot, "state-initialization.guard"), [1]);
        else
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = fault == "catalog" ? "DROP TABLE settings_extensions;" :
                "UPDATE schema_info SET instance_id='11111111-1111-1111-1111-111111111111';";
            command.ExecuteNonQuery();
        }
        using var lease = InstanceLease.Acquire(instance, new());
        var before = Fingerprints(instance.RuntimeRoot);
        Assert.Throws<IOException>(() => StateDatabase.OpenCurrent(instance, lease));
        Assert.Throws<IOException>(() => StateDatabase.CreateFresh(instance, lease));
        Assert.Equal(before, Fingerprints(instance.RuntimeRoot));
    }

    [Theory(DisplayName = "Legacy Single Database Files Remain Byte Identical When Every Startup And Maintenance Entry Refuses Them")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesLegacyFiles(bool hasGuard)
    {
        var root = NewRoot();
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var state = Path.Combine(instance.RuntimeRoot, "state"); Directory.CreateDirectory(state);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            File.WriteAllBytes(Path.Combine(state, "nexus.db" + suffix), Encoding.UTF8.GetBytes("legacy-preserved" + suffix));
        if (hasGuard) File.WriteAllBytes(Path.Combine(instance.RuntimeRoot, "state-initialization.guard"), []);
        using var lease = InstanceLease.Acquire(instance, new());
        var before = Fingerprints(instance.RuntimeRoot);
        var error = Assert.Throws<IOException>(() => StartupPresentation.Read(instance, lease));
        Assert.Equal("legacy_database_layout_requires_new_instance", error.Message);
        Assert.Equal(error.Message, Assert.Throws<IOException>(() => StateDatabase.OpenCurrent(instance, lease)).Message);
        Assert.Equal(error.Message, Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance,
            Path.Combine(root, "diagnostic-inspection"))).Message);
        var installer = new NexusPipeline.Platform.Storage.Installation.InstallerRuntimeAdmission(instance, new(),
            new(root, Path.Combine(instance.RuntimeRoot, "extensions")));
        Assert.Equal(error.Message, Assert.Throws<IOException>(() => installer.Open(lease,
            Path.Combine(root, "installer-inspection"))).Message);
        Assert.Equal(before, Fingerprints(instance.RuntimeRoot));
    }

    [Fact(DisplayName = "A Database Set Backup Retains One Committed Settings And Operation Snapshot")]
    public void KeepsConsistentBackup()
    {
        var (root, instance, database) = Create();
        var store = new HostSettingsStore(database);
        Save(store, 0, 30);
        var destination = Path.Combine(root, "backup"); Directory.CreateDirectory(destination);
        var backup = database.BackupTo(Path.Combine(destination, "runtime.db"));
        Save(store, 1, 90);
        Assert.Equal(8, backup.Files.Count);
        Assert.Equal(1L, Scalar(backup.Path, "SELECT count(*) FROM operations;"));
        Assert.Equal(30L, Scalar(Path.Combine(destination, "system.db"), "SELECT retention_days FROM settings_snapshots WHERE revision=1;"));
        var inspected = DatabaseInspector.Inspect(backup.Path, instance.InstanceId, Path.Combine(root, "inspection"));
        try { Assert.Equal(8, inspected.Databases.Count); }
        finally { inspected.OwnedCopy?.Discard(); }
        foreach (var file in backup.Files)
            Assert.Equal(file.Sha256, Hash(Path.Combine(destination, file.Name)));
        Assert.Throws<IOException>(() => database.BackupTo(backup.Path));
    }

    [Fact(DisplayName = "A Super Journal Reference Cannot Redirect Recovery Outside Its Database Set")]
    public void RejectsForeignJournal()
    {
        var (root, instance, database) = Create();
        var foreign = Path.Combine(root, "preserved-master"); File.WriteAllText(foreign, "preserve external bytes");
        var name = Encoding.UTF8.GetBytes(PhysicalPathPolicy.NativePath(foreign));
        var bytes = new byte[512 + name.Length + 16];
        byte[] magic = [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];
        magic.CopyTo(bytes, 0); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 512);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 4096); name.CopyTo(bytes, 512);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(512 + name.Length), (uint)name.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(516 + name.Length), (uint)name.Sum(value => (sbyte)value));
        magic.CopyTo(bytes, bytes.Length - 8);
        File.WriteAllBytes(database.DatabasePath + "-journal", bytes);
        var before = Fingerprints(instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        Assert.Throws<IOException>(() => StateDatabase.OpenCurrent(instance, lease));
        Assert.Throws<IOException>(() => DatabaseInspector.Inspect(database.DatabasePath, instance.InstanceId, Path.Combine(root, "inspection")));
        Assert.Equal(before.Where(line => !line.StartsWith("instance.lock:", StringComparison.Ordinal)),
            Fingerprints(instance.RuntimeRoot).Where(line => !line.StartsWith("instance.lock:", StringComparison.Ordinal)));
        Assert.Equal("preserve external bytes", File.ReadAllText(foreign));
    }

    [Theory(DisplayName = "A Stopped Writer Recovers All Databases Before During Or After Native Commit")]
    [InlineData("before", 0L)]
    [InlineData("during", 0L)]
    [InlineData("after", 1L)]
    public async Task RecoversInterruptedSet(string phase, long expected)
    {
        var root = NewRoot();
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var report = Path.Combine(root, "checkpoint.json");
        using var child = new OwnedCrashProcess();
        child.Start(["database-transaction", instance.RuntimeRoot, phase, report], root, instance.InstanceId, Guid.NewGuid(),
            Path.Combine(root, "creator.json"));
        var reached = await child.ReadReportAsync(report, TimeSpan.FromSeconds(25));
        Assert.Equal(phase, reached.GetProperty("phase").GetString());
        Assert.True((await child.StopAsync()).CompleteJobStopped);
        if (phase == "during")
        {
            var before = Fingerprints(instance.RuntimeRoot);
            var inspected = DatabaseInspector.Inspect(Path.Combine(instance.RuntimeRoot, "state", "runtime.db"),
                instance.InstanceId, Path.Combine(root, "cold-copy"));
            try
            {
                Assert.Equal(0L, Scalar(Path.Combine(inspected.DiagnosticCopyRoot, "runtime.db"), "SELECT count(*) FROM operations;"));
                Assert.Equal(before, Fingerprints(instance.RuntimeRoot));
            }
            finally { inspected.OwnedCopy?.Discard(); }
        }
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenCurrent(instance, lease);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM main.operations),(SELECT count(*) FROM extensions.extension_documents)," +
            "(SELECT count(*) FROM system.startup_registration_current WHERE command_json IS NOT NULL);";
        using var rows = command.ExecuteReader(); Assert.True(rows.Read());
        for (var column = 0; column < 3; column++) Assert.Equal(expected, rows.GetInt64(column));
    }

    private static void InsertDocument(SqliteConnection connection, SqliteTransaction transaction, Guid operation)
    {
        var content = "{}"u8.ToArray();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO extensions.extension_documents VALUES('fixture','settings',1,$content,$digest,$operation);";
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$digest", Convert.ToHexStringLower(SHA256.HashData(content)));
        command.Parameters.AddWithValue("$operation", operation.ToString("D")); command.ExecuteNonQuery();
    }

    private static void Save(HostSettingsStore store, long revision, int days)
    {
        var change = new HostSettingsChange(revision, HostSettings.Default with { HistoryRetentionDays = days });
        store.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
    }

    private static object? Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static (string Root, RuntimeInstance Instance, StateDatabase Database) Create()
    {
        var root = NewRoot();
        var instance = new InstanceRoot(new ContractReader()).OpenOrCreate(Path.Combine(root, "runtime"));
        return (root, instance, StateDatabase.CreateFresh(instance));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Explicit test root required"),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static string[] Fingerprints(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "instance.lock")
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Hash(path)).ToArray();
}
