using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed record DatabaseSettings(string JournalMode, long Synchronous, long ForeignKeys, string NativeVersion);
public sealed record DatabaseBackup(string Path, Guid InstanceId, long SizeBytes, string Sha256);

public sealed class StateDatabase
{
    private readonly DatabaseWriteGate _writer = new(64, TimeSpan.FromSeconds(2));
    private readonly bool _readOnly;
    public string DatabasePath { get; }
    public string RuntimeRoot { get; }
    public Guid InstanceId { get; }
    public static string SchemaDigest => DatabaseSchema.Digest;
    private long _commitSequence;
    public long CommitSequence => Interlocked.Read(ref _commitSequence);
    internal Execution.AttemptReadRegistry? AttemptReads { get; set; }
    internal BlobReferenceFacts ReferenceFacts { get; } = new();

    private StateDatabase(string path, Guid instanceId, string runtimeRoot, bool readOnly = false)
    {
        DatabasePath = path; InstanceId = instanceId; RuntimeRoot = runtimeRoot;
        _readOnly = readOnly;
    }

    internal static StateDatabase ObserveMaintenancePeer(RuntimeInstance instance)
    {
        var actual = new InstanceRoot(new()).OpenExisting(instance.RuntimeRoot);
        if (actual != instance) throw new IOException("runtime_identity_mismatch");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "state", "nexus.db"));
        var inspected = DatabaseInspector.Inspect(path, instance.InstanceId,
            Path.Combine(instance.RuntimeRoot, "diagnostics", "maintenance-peer-inspection"));
        try
        {
            if (inspected.SchemaDigest != DatabaseSchema.Digest) throw new IOException("unsupported_database_identity");
        }
        finally { inspected.OwnedCopy?.Discard(); }
        return new(path, instance.InstanceId, instance.RuntimeRoot, readOnly: true);
    }

    internal static StateDatabase Inspected(DatabaseInspection inspection, RuntimeInstance instance)
    {
        if (inspection.InstanceId != instance.InstanceId) throw new IOException("database_inspection_instance_unbound");
        if (inspection.SchemaDigest != DatabaseSchema.Digest) throw new IOException("unsupported_database_catalog");
        return new(Path.Combine(inspection.DiagnosticCopyRoot, "nexus.db"), inspection.InstanceId, instance.RuntimeRoot, readOnly: true);
    }

    public static StateDatabase OpenExisting(RuntimeInstance instance, string diagnosticRoot,
        IDatabaseStartupAdmission? admission = null, InstanceLease? startupLease = null)
    {
        using var profile = StartupProfile.Begin("database.admission");
        var actual = new InstanceRoot(new ContractReader()).OpenExisting(instance.RuntimeRoot);
        if (actual.InstanceId != instance.InstanceId) throw new IOException("runtime_identity_mismatch");
        var guard = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(actual.RuntimeRoot, "state-initialization.guard"));
        using (var input = new FileStream(guard, FileMode.Open, FileAccess.Read, FileShare.Read))
            if (input.Length != 0) throw new IOException("unrecognized_state_initialization_guard");
        var databasePath = Path.Combine(actual.RuntimeRoot, "state", "nexus.db");
        var inspected = DatabaseInspector.Inspect(databasePath, actual.InstanceId, diagnosticRoot);
        if (admission is not null)
        {
            using var reads = DatabaseReadScope.Hold(Path.Combine(inspected.DiagnosticCopyRoot, "nexus.db"));
            admission.Verify(inspected);
        }
        var evidence = inspected.SourceFiles.ToDictionary(item => item.Suffix, StringComparer.Ordinal);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(databasePath + suffix);
            if (File.Exists(path) != evidence.ContainsKey(suffix)) throw new IOException("database_group_changed_after_inspection");
            if (!evidence.TryGetValue(suffix, out var expected)) continue;
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length != expected.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(source)) != expected.Sha256)
                throw new IOException("database_group_changed_after_inspection");
        }
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "startup.database-source-recheck",
            digestReadBytes = evidence.Values.Sum(file => file.SizeBytes) }));
        if (startupLease is not null)
        {
            startupLease.VerifyHeld(actual);
            using var connection = DatabaseConnections.Open(databasePath);
            using var transaction = connection.BeginTransaction(deferred: false);
            if (DatabaseSchema.VerifyCatalog(connection, transaction) != DatabaseSchema.Digest)
                throw new IOException("database_group_changed_after_inspection");
            DatabaseReadIndexes.Ensure(connection, transaction);
            startupLease.VerifyHeld(actual);
            transaction.Commit();
        }
        inspected.OwnedCopy?.Discard();
        startupLease?.BindDatabase();
        return new(databasePath, actual.InstanceId, actual.RuntimeRoot);
    }

    public static StateDatabase OpenCurrent(RuntimeInstance instance, InstanceLease startupLease)
    {
        using var profile = StartupProfile.Begin("database.admission");
        startupLease.VerifyHeld(instance);
        var actual = new InstanceRoot(new()).OpenExisting(instance.RuntimeRoot);
        if (actual != instance) throw new IOException("runtime_identity_mismatch");
        var guard = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(actual.RuntimeRoot, "state-initialization.guard"));
        using (var input = new FileStream(guard, FileMode.Open, FileAccess.Read, FileShare.Read))
            if (input.Length != 0) throw new IOException("unrecognized_state_initialization_guard");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(actual.RuntimeRoot, "state", "nexus.db"));
        using (var connection = DatabaseConnections.Open(path, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            var digest = DatabaseSchema.VerifyCatalog(connection, transaction);
            DatabaseInspector.VerifyIdentity(connection, actual.InstanceId, digest, transaction);
            transaction.Commit();
        }
        using (var connection = DatabaseConnections.Open(path))
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            DatabaseReadIndexes.Ensure(connection, transaction);
            startupLease.VerifyHeld(actual);
            transaction.Commit();
        }
        startupLease.BindDatabase();
        return new(path, actual.InstanceId, actual.RuntimeRoot);
    }

    internal T Write<T>(Func<SqliteConnection, SqliteTransaction, T> operation)
    {
        if (_readOnly) throw new IOException("maintenance_observation_is_read_only");
        using var lease = _writer.Enter();
        var borrowed = DatabaseWriteScope.Borrow(DatabasePath);
        using var opened = borrowed is null ? DatabaseConnections.Open(DatabasePath) : null;
        var connection = borrowed ?? opened!;
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = operation(connection, transaction);
        transaction.Commit();
        Interlocked.Increment(ref _commitSequence);
        return result;
    }

    public static StateDatabase CreateFresh(RuntimeInstance instance, InstanceLease? startupLease = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows runtime required");
        var verified = new InstanceRoot(new ContractReader()).OpenExisting(instance.RuntimeRoot);
        if (verified.InstanceId != instance.InstanceId)
            throw new IOException("runtime_identity_mismatch");
        startupLease?.VerifyHeld(verified);
        var directory = Path.Combine(instance.RuntimeRoot, "state");
        var path = Path.Combine(directory, "nexus.db");
        DatabaseConnections.RejectLinks(path);
        var initialEntries = new HashSet<string>(["instance.json", "instance.lock", "state"], StringComparer.OrdinalIgnoreCase);
        if (Directory.EnumerateFileSystemEntries(instance.RuntimeRoot).Any(entry => !initialEntries.Contains(Path.GetFileName(entry))))
            throw new IOException("Runtime is not an uninitialized instance");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("State directory is not empty");
        // This guard outlives the database, so lost state cannot look like a fresh installation.
        using (var guard = new FileStream(Path.Combine(instance.RuntimeRoot, "state-initialization.guard"), FileMode.CreateNew,
                   FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
            guard.Flush(flushToDisk: true);
        Directory.CreateDirectory(directory);
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using var connection = DatabaseConnections.Open(path);
        if (!string.Equals(Convert.ToString(DatabaseConnections.Scalar(connection, "PRAGMA journal_mode=WAL;"), CultureInfo.InvariantCulture), "wal", StringComparison.Ordinal))
            throw new IOException("Required WAL mode is unavailable");
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = DatabaseSchema.Sql;
        command.ExecuteNonQuery();
        DatabaseReadIndexes.Ensure(connection, transaction);
        command.CommandText = "INSERT INTO schema_info VALUES(1,'nexus.state.database',1,$instance,$digest,$created);";
        command.Parameters.AddWithValue("$instance", instance.InstanceId.ToString("D"));
        command.Parameters.AddWithValue("$digest", SchemaDigest);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        HostSettingsRows.Insert(connection, transaction, new(0, NexusPipeline.Core.Settings.HostSettings.Default));
        HostSettingsRows.Execute(connection, transaction, "INSERT INTO settings_current VALUES(1,0);");
        HostSettingsRows.Execute(connection, transaction, "INSERT INTO startup_registration_current VALUES(1,NULL);");
        NexusPipeline.Platform.Storage.Secrets.SecretReplayProof.Initialize(instance, connection, transaction);
        transaction.Commit();
        startupLease?.BindDatabase();
        return new(path, instance.InstanceId, instance.RuntimeRoot);
    }

    public DatabaseSettings ReadSettings()
    {
        using var connection = DatabaseConnections.Open(DatabasePath);
        return new(Convert.ToString(DatabaseConnections.Scalar(connection, "PRAGMA journal_mode;"), CultureInfo.InvariantCulture)!,
            Convert.ToInt64(DatabaseConnections.Scalar(connection, "PRAGMA synchronous;"), CultureInfo.InvariantCulture),
            Convert.ToInt64(DatabaseConnections.Scalar(connection, "PRAGMA foreign_keys;"), CultureInfo.InvariantCulture),
            Convert.ToString(DatabaseConnections.Scalar(connection, "SELECT sqlite_version();"), CultureInfo.InvariantCulture)!);
    }

    public OperationAcceptance Accept(OperationIdentity identity, DateTimeOffset acceptedAt)
        => Write((connection, transaction) => OperationRows.Accept(connection, transaction, identity, acceptedAt));

    public DatabaseCheckpoint Checkpoint(CheckpointMode mode = CheckpointMode.Passive)
    {
        using var lease = _writer.Enter();
        using var connection = DatabaseConnections.Open(DatabasePath);
        return DatabaseCheckpointReader.Execute(connection, mode);
    }

    public DatabaseBackup BackupTo(string destinationPath)
    {
        var destination = Path.GetFullPath(destinationPath);
        DatabaseConnections.RejectLinks(destination);
        if (string.Equals(destination, DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Backup destination cannot be the live database");
        using var lease = _writer.Enter();
        {
            using (new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using (var source = DatabaseConnections.Open(DatabasePath))
            using (var backup = DatabaseConnections.Open(destination))
                source.BackupDatabase(backup);
            using var file = File.OpenRead(destination);
            return new(destination, InstanceId, file.Length, Convert.ToHexStringLower(SHA256.HashData(file)));
        }
    }
}
