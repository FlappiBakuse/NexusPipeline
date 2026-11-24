using System.Globalization;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed record DatabaseSettings(string JournalMode, long Synchronous, long ForeignKeys, string NativeVersion);
public sealed record DatabaseBackupFile(string Name, long SizeBytes, string Sha256);
public sealed record DatabaseBackup(string Path, Guid InstanceId, long SizeBytes, string Sha256)
{
    public IReadOnlyList<DatabaseBackupFile> Files { get; init; } = [];
}

public sealed class StateDatabase
{
    private readonly DatabaseWriteGate _writer = new(64, TimeSpan.FromSeconds(2));
    private readonly bool _readOnly;
    private readonly long[] _domainSequences = new long[8];
    private long _committedReadSequence;
    private ControlStatisticsVersion _controlStatisticsVersion;
    public string DatabasePath { get; }
    public string RuntimeRoot { get; }
    public Guid InstanceId { get; }
    public static string SchemaDigest => DatabaseSchema.Digest;
    private long _commitSequence;
    public long CommitSequence => Interlocked.Read(ref _commitSequence);
    internal Execution.AttemptReadRegistry? AttemptReads { get; set; }
    internal InstanceLease? RuntimeLease { get; set; }
    internal Processes.ProcessIdentityLease? ExecutorIdentity => OperatingSystem.IsWindows() ? RuntimeLease?.ExecutorIdentity : null;
    internal BlobReferenceFacts ReferenceFacts { get; } = new();
    internal ConfigurationCleanupInbox ConfigurationCleanup { get; } = new();

    private StateDatabase(string path, Guid instanceId, string runtimeRoot, bool readOnly = false)
    { DatabasePath = path; InstanceId = instanceId; RuntimeRoot = runtimeRoot; _readOnly = readOnly; }

    internal static StateDatabase ObserveMaintenancePeer(RuntimeInstance instance)
    {
        var actual = new InstanceRoot(new()).OpenExisting(instance.RuntimeRoot);
        if (actual != instance) throw new IOException("runtime_identity_mismatch");
        var path = DatabaseLayout.RuntimePath(actual);
        DatabaseLayout.RequireFiles(path);
        try
        {
            // The parent still owns its write connections. Use SQLite's coordinated read snapshot until it releases the runtime.
            using var connection = DatabaseConnections.Open(path, readOnly: true, allowPooling: false);
            using var snapshot = DatabaseConnections.BeginRead(connection);
            DatabaseSchema.VerifyCatalog(connection, snapshot);
            DatabaseInspector.VerifyIdentity(connection, actual.InstanceId, transaction: snapshot);
            DatabaseInspector.VerifyIntegrity(connection, snapshot);
            snapshot.Commit();
        }
        catch (SqliteException error) { throw new IOException("unrecognized_or_corrupt_database", error); }
        return new(path, actual.InstanceId, actual.RuntimeRoot, readOnly: true);
    }

    internal static StateDatabase Inspected(DatabaseInspection inspection, RuntimeInstance instance)
    {
        if (inspection.InstanceId != instance.InstanceId || inspection.SchemaDigest != SchemaDigest)
            throw new IOException("database_inspection_instance_unbound");
        return new(Path.Combine(inspection.DiagnosticCopyRoot, "runtime.db"), inspection.InstanceId, instance.RuntimeRoot, readOnly: true);
    }

    public static StateDatabase OpenExisting(RuntimeInstance instance, string diagnosticRoot,
        IDatabaseStartupAdmission? admission = null, InstanceLease? startupLease = null)
    {
        using var profile = StartupProfile.Begin("database.admission");
        var actual = RequireExisting(instance, startupLease);
        var path = DatabaseLayout.RuntimePath(actual);
        var inspected = DatabaseInspector.Inspect(path, actual.InstanceId, diagnosticRoot);
        if (admission is not null)
        {
            using var reads = DatabaseReadScope.Hold(Path.Combine(inspected.DiagnosticCopyRoot, "runtime.db"));
            admission.Verify(inspected);
        }
        DatabaseInspector.RecheckSource(path, inspected.SourceFiles);
        DatabaseJournals.VerifyPaths(path);
        using (var connection = DatabaseConnections.Open(path))
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            DatabaseSchema.VerifyCatalog(connection, transaction);
            DatabaseInspector.VerifyIdentity(connection, actual.InstanceId, transaction: transaction);
            DatabaseReadIndexes.Ensure(connection, transaction);
            startupLease?.VerifyHeld(actual);
            transaction.Commit();
        }
        inspected.OwnedCopy?.Discard();
        return Attach(path, actual, startupLease);
    }

    public static StateDatabase OpenCurrent(RuntimeInstance instance, InstanceLease startupLease)
    {
        using var profile = StartupProfile.Begin("database.admission");
        startupLease.VerifyHeld(instance);
        if (startupLease.Database is { } current) return current;
        var actual = RequireExisting(instance, startupLease);
        var path = DatabaseLayout.RuntimePath(actual);
        DatabaseLayout.RequireFiles(path);
        try
        {
            foreach (var domain in DatabaseLayout.Domains)
            {
                using var frozen = DatabaseConnections.OpenFrozen(DatabaseLayout.PathFor(path, domain));
                DatabaseSchema.VerifyDomain(frozen, domain, standalone: true);
                DatabaseInspector.VerifyDomainIdentity(frozen, actual.InstanceId, domain, standalone: true);
            }
            DatabaseJournals.VerifyPaths(path);
            using (var connection = DatabaseConnections.Open(path))
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                DatabaseSchema.VerifyCatalog(connection, transaction);
                DatabaseInspector.VerifyIdentity(connection, actual.InstanceId, transaction: transaction);
                DatabaseReadIndexes.Ensure(connection, transaction);
                startupLease.VerifyHeld(actual);
                transaction.Commit();
            }
            return Attach(path, actual, startupLease);
        }
        catch (SqliteException error) { throw new IOException("unrecognized_or_corrupt_database", error); }
    }

    private static StateDatabase Attach(string path, RuntimeInstance instance, InstanceLease? lease)
    {
        if (lease?.Database is { } current)
        {
            DatabaseLayout.RequireInstance(current, instance);
            return current;
        }
        var database = new StateDatabase(path, instance.InstanceId, instance.RuntimeRoot) { RuntimeLease = lease };
        lease?.BindDatabase(database);
        return database;
    }

    private static RuntimeInstance RequireExisting(RuntimeInstance instance, InstanceLease? lease)
    {
        lease?.VerifyHeld(instance);
        var actual = new InstanceRoot(new ContractReader()).OpenExisting(instance.RuntimeRoot);
        if (actual != instance) throw new IOException("runtime_identity_mismatch");
        var guard = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(actual.RuntimeRoot, "state-initialization.guard"));
        try
        {
            using var input = new FileStream(guard, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != 0) throw new IOException("state_initialization_incomplete");
        }
        catch (FileNotFoundException) when (File.Exists(Path.Combine(actual.RuntimeRoot, "state", "nexus.db")))
        { throw new IOException("legacy_database_layout_requires_new_instance"); }
        return actual;
    }

    internal T Write<T>(Func<SqliteConnection, SqliteTransaction, T> operation)
    {
        if (_readOnly) throw new IOException("maintenance_observation_is_read_only");
        using var lease = _writer.Enter();
        var borrowed = DatabaseWriteScope.Borrow(DatabasePath);
        using var opened = borrowed is null ? DatabaseConnections.Open(DatabasePath) : null;
        var connection = borrowed ?? opened!;
        using var transaction = connection.BeginTransaction(deferred: false);
        using var changes = new DatabaseChanges(connection);
        var result = operation(connection, transaction);
        DatabaseRelations.VerifyChanges(connection, transaction, changes);
        var versioned = (changes.Domains & (DatabaseDomain.Runtime | DatabaseDomain.Extensions | DatabaseDomain.System)) != 0 ||
            changes.ControlStatistics != 0;
        // An odd sequence keeps readers from reusing cached facts during COMMIT and version publication.
        if (versioned) Interlocked.Increment(ref _committedReadSequence);
        try { CommitChanges(transaction, changes); }
        finally { if (versioned) Interlocked.Increment(ref _committedReadSequence); }
        return result;
    }

    internal ControlStatisticsVersion? ReadControlVersion() =>
        ReadCommittedVersion(static database => database._controlStatisticsVersion);

    internal DatabaseVersion? ReadInstallationVersion(DatabaseDomain domains) =>
        ReadCommittedVersion(database => DatabaseVersion.Read(database, null, domains));

    private T? ReadCommittedVersion<T>(Func<StateDatabase, T> read) where T : struct
    {
        var sequence = Interlocked.Read(ref _committedReadSequence);
        if ((sequence & 1) != 0) return null;
        var result = read(this);
        return Interlocked.Read(ref _committedReadSequence) == sequence ? result : null;
    }

    private void CommitChanges(SqliteTransaction transaction, DatabaseChanges changes)
    {
        // A busy COMMIT leaves the transaction active; retry it without repeating the operation.
        for (var attempt = 0; ; attempt++)
        {
            try { transaction.Commit(); break; }
            catch (SqliteException error) when (error.SqliteErrorCode == 5 && attempt < 2)
            { Thread.Sleep(50); }
        }
        if (changes.Domains != 0)
        {
            ConfigurationCleanup.Record(changes.Rows);
            if (changes.ControlStatistics != 0)
                _controlStatisticsVersion = _controlStatisticsVersion.Advance(changes.ControlStatistics);
            for (var index = 0; index < DatabaseLayout.Domains.Length; index++)
                if ((changes.Domains & DatabaseLayout.Domains[index]) != 0) Interlocked.Increment(ref _domainSequences[index]);
            Interlocked.Increment(ref _commitSequence);
        }
    }

    internal long DomainSequence(DatabaseDomain domain) => Interlocked.Read(ref _domainSequences[Array.IndexOf(DatabaseLayout.Domains, domain)]);

    private static bool IsDesktopProfile(string entry) =>
        StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(entry), RuntimeInstance.DesktopProfileDirectory) &&
        (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    public static StateDatabase CreateFresh(RuntimeInstance instance, InstanceLease? startupLease = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows runtime required");
        var verified = new InstanceRoot(new ContractReader()).OpenExisting(instance.RuntimeRoot);
        if (verified != instance) throw new IOException("runtime_identity_mismatch");
        startupLease?.VerifyHeld(verified);
        var directory = Path.Combine(instance.RuntimeRoot, "state");
        DatabaseConnections.RejectLinks(directory);
        if (File.Exists(Path.Combine(directory, "nexus.db"))) throw new IOException("legacy_database_layout_requires_new_instance");
        var initialEntries = new HashSet<string>(["instance.json", "instance.lock", "state"], StringComparer.OrdinalIgnoreCase);
        if (Directory.EnumerateFileSystemEntries(instance.RuntimeRoot).Any(entry => !initialEntries.Contains(Path.GetFileName(entry)) &&
            !IsDesktopProfile(entry)) ||
            Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("runtime_not_uninitialized");
        using var guard = new FileStream(Path.Combine(instance.RuntimeRoot, "state-initialization.guard"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
        guard.WriteByte(1); guard.Flush(flushToDisk: true);
        Directory.CreateDirectory(directory);
        var path = DatabaseLayout.RuntimePath(instance);
        foreach (var domain in DatabaseLayout.Domains)
            using (new FileStream(DatabaseLayout.PathFor(path, domain), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using (var connection = DatabaseConnections.Open(path, creating: true))
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            foreach (var domain in DatabaseLayout.Domains)
            {
                DatabaseSchema.CreateInside(connection, transaction, domain);
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = $"INSERT INTO {DatabaseLayout.Alias(domain)}.schema_info VALUES(1,'nexus.state.domain',2,$instance,$domain,$digest,$created);";
                command.Parameters.AddWithValue("$instance", instance.InstanceId.ToString("D"));
                command.Parameters.AddWithValue("$domain", DatabaseLayout.Name(domain));
                command.Parameters.AddWithValue("$digest", DatabaseSchema.DomainDigest(domain));
                command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
            DatabaseReadIndexes.Ensure(connection, transaction);
            HostSettingsRows.Insert(connection, transaction, new(0, NexusPipeline.Core.Settings.HostSettings.Default));
            HostSettingsRows.Execute(connection, transaction, "INSERT INTO system.settings_current VALUES(1,0);");
            HostSettingsRows.Execute(connection, transaction, "INSERT INTO system.startup_registration_current VALUES(1,NULL);");
            Secrets.SecretReplayProof.Initialize(instance, connection, transaction);
            transaction.Commit();
        }
        guard.SetLength(0); guard.Flush(flushToDisk: true);
        return Attach(path, instance, startupLease);
    }

    public DatabaseSettings ReadSettings()
    {
        using var connection = DatabaseConnections.Open(DatabasePath, domains: DatabaseDomain.All);
        return new(Convert.ToString(DatabaseConnections.Scalar(connection, "PRAGMA main.journal_mode;"), CultureInfo.InvariantCulture)!,
            Convert.ToInt64(DatabaseConnections.Scalar(connection, "PRAGMA main.synchronous;"), CultureInfo.InvariantCulture),
            Convert.ToInt64(DatabaseConnections.Scalar(connection, "PRAGMA foreign_keys;"), CultureInfo.InvariantCulture),
            Convert.ToString(DatabaseConnections.Scalar(connection, "SELECT sqlite_version();"), CultureInfo.InvariantCulture)!);
    }

    public OperationAcceptance Accept(OperationIdentity identity, DateTimeOffset acceptedAt)
        => Write((connection, transaction) => OperationRows.Accept(connection, transaction, identity, acceptedAt));

    public DatabaseBackup BackupTo(string destinationPath)
    {
        using var lease = _writer.Enter();
        return DatabaseBackupStore.Create(this, destinationPath);
    }
}
