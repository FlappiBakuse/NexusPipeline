using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DatabaseScopeTests
{
    [Fact(DisplayName = "Eight Concurrent Read Roles Keep The Retained Page Budget And Observe Writes Before All Handles Release")]
    public void BoundsConcurrentReaders()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "pooled-readers-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(root);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.CreateFresh(instance, lease);
        var readers = new List<SqliteConnection>();
        try
        {
            foreach (var domain in new[]
            {
                DatabaseDomain.Runtime,
                DatabaseDomain.Runtime | DatabaseDomain.Configuration,
                DatabaseDomain.Runtime | DatabaseDomain.Records,
                DatabaseDomain.Runtime | DatabaseDomain.System,
                DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Records | DatabaseDomain.System,
                DatabaseDomain.Runtime | DatabaseDomain.Extensions | DatabaseDomain.System,
                DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Content,
                DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content
            })
                readers.Add(DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: domain));
            var readerBudget = readers.Sum(connection =>
            {
                var budget = CacheBudget(connection);
                Assert.InRange(budget, 1, 3072);
                return budget;
            });
            var writerBudget = database.Write((connection, transaction) =>
            {
                using var command = DatabaseConnections.Command(connection, transaction, "PRAGMA main.synchronous;");
                Assert.Equal(2L, command.ExecuteScalar());
                command.CommandText = "PRAGMA foreign_keys;";
                Assert.Equal(1L, command.ExecuteScalar());
                return CacheBudget(connection, transaction);
            });
            Assert.InRange(writerBudget, 1, 8192);
            Assert.InRange(readerBudget + writerBudget, 1, 32768);
            using (var overflow = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime))
            {
                Assert.InRange(CacheBudget(overflow), 1, 8192);
                Assert.Equal(0L, DatabaseConnections.Scalar(overflow, "SELECT count(*) FROM main.operations;"));
                Assert.True(database.Accept(Operation(), DateTimeOffset.UtcNow).IsNew);
                foreach (var connection in readers.Append(overflow))
                {
                    Assert.Equal(1L, DatabaseConnections.Scalar(connection, "SELECT count(*) FROM main.operations;"));
                    using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM main.operations;";
                    Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
                }
            }
        }
        finally { foreach (var connection in readers) connection.Dispose(); }
        lease.Dispose();
        Directory.Move(root, root + "-released");
    }

    [Fact(DisplayName = "Scoped Recovery Snapshots Observe External Commits And Never Share Facts Between Instances")]
    public void RefreshesHistorySnapshots()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "history-snapshots-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "first"));
        var other = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "other"));
        var database = StateDatabase.CreateFresh(instance); var foreign = StateDatabase.CreateFresh(other);
        var registry = new RecoveryJournalRegistry(database);
        var original = registry.ReadActiveSnapshot();
        using (new HistoryReadScope(instance, database, new()))
        {
            Assert.Equal(original.Revision, registry.ReadActiveSnapshot().Revision);
            using (var connection = DatabaseConnections.Open(database.DatabasePath, domains: DatabaseDomain.Runtime))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE main.recovery_index_state SET revision=revision+1 WHERE singleton=1;";
                command.ExecuteNonQuery();
            }
            Assert.Equal(original.Revision + 1, registry.ReadActiveSnapshot().Revision);
            Assert.Equal(original.Revision, new RecoveryJournalRegistry(foreign).ReadActiveSnapshot().Revision);
            Assert.Equal(original.Revision + 1, registry.ReadActiveSnapshot().Revision);
        }
        Directory.Move(instance.RuntimeRoot, instance.RuntimeRoot + "-released");
    }

    [Fact(DisplayName = "Publication Commits Stay Durable And A Failed Transaction Does Not Leak Into Its Successor")]
    public void PreservesWriteBoundaries()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "publication-sql-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(root);
        var database = StateDatabase.CreateFresh(instance);
        var accepted = new OperationIdentity(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.command");
        var rejected = new OperationIdentity(Guid.NewGuid(), "fixture", new string('b', 64), "fixture.command");
        var following = new OperationIdentity(Guid.NewGuid(), "fixture", new string('c', 64), "fixture.command");
        using (var lease = InstanceLease.Acquire(instance, new()))
            lease.PublishExecution(() =>
            {
                Assert.True(database.Accept(accepted, DateTimeOffset.UtcNow).IsNew);
                Assert.Throws<IOException>(() => database.Write<bool>((connection, transaction) =>
                {
                    _ = OperationRows.Accept(connection, transaction, rejected, DateTimeOffset.UtcNow);
                    throw new IOException("fixture_rollback");
                }));
                Assert.True(database.Accept(following, DateTimeOffset.UtcNow).IsNew);
            });
        var reopened = StateDatabase.OpenExisting(instance, Path.Combine(root, "diagnostics", "reopen"));
        Assert.False(reopened.Accept(accepted, DateTimeOffset.UtcNow).IsNew);
        Assert.False(reopened.Accept(following, DateTimeOffset.UtcNow).IsNew);
        Assert.True(reopened.Accept(rejected, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(2, reopened.ReadSettings().Synchronous);
        Directory.Move(root, root + "-released");
    }

    [Fact(DisplayName = "Historical Binding Readers Observe New Commits And Keep Nested Instances And File Lifetimes Separate")]
    public void IsolatesHistoryReaders()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "history-sql-" + Guid.NewGuid().ToString("N"));
        var contracts = new NexusPipeline.Platform.Serialization.ContractReader();
        var first = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "first"));
        var second = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "second"));
        var a = StateDatabase.CreateFresh(first); var b = StateDatabase.CreateFresh(second);
        var firstOperation = new OperationIdentity(Guid.NewGuid(), "first-reader", new string('a', 64), "fixture.history");
        var secondOperation = new OperationIdentity(Guid.NewGuid(), "second-reader", new string('b', 64), "fixture.history");
        b.Accept(secondOperation, DateTimeOffset.UtcNow);
        OperationSnapshot? Find(StateDatabase database, OperationIdentity operation) => HistoryReadScope.ReadDatabase(database,
            (connection, transaction) => OperationRows.ReadOwned(connection, transaction, operation.OperationId, operation.SubjectId));
        var expectedFirst = new OperationSnapshot(firstOperation.OperationId, firstOperation.Kind, "accepted", null);
        var expectedSecond = new OperationSnapshot(secondOperation.OperationId, secondOperation.Kind, "accepted", null);
        using (var outer = new HistoryReadScope(first, a, contracts))
        {
            Assert.Null(Find(a, firstOperation));
            using (var inner = new HistoryReadScope(second, b, contracts))
            {
                Assert.Equal(expectedSecond, Find(b, secondOperation));
                Assert.Equal("history_read_scope_order", Assert.Throws<InvalidOperationException>(() => outer.Dispose()).Message);
                a.Accept(firstOperation, DateTimeOffset.UtcNow);
                Assert.Equal(expectedFirst, Find(a, firstOperation));
                Assert.Null(Find(a, secondOperation));
                Assert.Equal(expectedSecond, Find(b, secondOperation));
                Assert.Null(Find(b, firstOperation));
                Assert.Throws<IOException>(() => File.Delete(b.DatabasePath));
            }
            Assert.Equal(expectedFirst, Find(a, firstOperation));
            Assert.Throws<IOException>(() => File.Delete(a.DatabasePath));
            Directory.Move(second.RuntimeRoot, second.RuntimeRoot + "-released");
        }
        Directory.Move(first.RuntimeRoot, first.RuntimeRoot + "-released");
    }

    [Fact(DisplayName = "Protected Installation Reads Observe External Commits And Retain No Failed Validation Or Database Lock")]
    public void RechecksCommittedChanges()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "database-validation-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(root);
        var database = StateDatabase.CreateFresh(instance);
        var path = database.DatabasePath;
        void Validate(SqliteConnection connection, SqliteTransaction transaction)
        {
            if (ReadValue(connection, transaction) != 1) throw new IOException("changed_installation");
        }
        void Write(long value)
        {
            using var connection = DatabaseConnections.Open(path, domains: DatabaseDomain.System);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE system.settings_snapshots SET retention_days=$number WHERE revision=0;";
            command.Parameters.AddWithValue("$number", value); command.ExecuteNonQuery();
        }
        Write(1);
        using (var held = new DatabaseValidationLease(database, domains: DatabaseDomain.System))
        {
            long Observe() => held.Read((connection, transaction) => ReadValue(connection, transaction));
            Assert.Equal(1, Observe());
            held.RequireCurrent(Validate);
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
            Write(2);
            Assert.Equal(2, Observe());
            Assert.Equal("changed_installation", Assert.Throws<IOException>(() => held.RequireCurrent(Validate)).Message);
            Write(1); held.RequireCurrent(Validate);
            Assert.Equal(1, Observe());
            Assert.Throws<SqliteException>(() => held.Read((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE system.settings_snapshots SET retention_days=9 WHERE revision=0;";
                return command.ExecuteNonQuery();
            }));
            Assert.Equal(1, Observe());
        }
        var moved = root + "-moved";
        Directory.Move(root, moved);
        using var final = DatabaseConnections.Open(Path.Combine(moved, "state", "runtime.db"), readOnly: true, domains: DatabaseDomain.System);
        Assert.Equal(1, ReadValue(final, null));
    }

    [Fact(DisplayName = "An Installation Lease Rechecks Its Bound Operation And The Current Extension Preference")]
    [SupportedOSPlatform("windows")]
    public async Task SeparatesInstallationReads()
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: true, managedMode: "frontend");
        _ = await FrontendInstalledTests.OpenAsync(fixture);
        var catalog = fixture.Package.Catalog;
        var database = catalog.Database;
        try
        {
            var source = new ExtensionInstalledEvidence(catalog.Instance, database, catalog.Contracts, fixture.Roots);
            using var proof = Assert.IsType<InstallationReadLease>(source.ProtectCurrent("fixture-plugin", catalog.Lease));
            var original = proof.ReadCurrent();
            Assert.Equal(fixture.Accepted.JournalId, original.Registered.JournalId);
            Assert.Equal(fixture.Package.OperationId, original.Registered.OperationId);
            var manifestPath = Path.Combine(fixture.Target, "manifest.json");
            var manifest = File.ReadAllBytes(manifestPath);
            var physical = PhysicalObjectIdentity.Read(manifestPath);

            Assert.True(database.Accept(Operation(), DateTimeOffset.UtcNow).IsNew);
            Assert.Equal(original.PlanReference, proof.ReadCurrent().PlanReference);
            void ChangeBoundOperation(string before, string after) => Assert.Equal(1, database.Write((connection, transaction) =>
            {
                using var change = DatabaseConnections.Command(connection, transaction,
                    "UPDATE main.operations SET status=$after WHERE operation_id=$id AND status=$before;");
                change.Parameters.AddWithValue("$id", fixture.Package.OperationId.ToString("D"));
                change.Parameters.AddWithValue("$before", before);
                change.Parameters.AddWithValue("$after", after);
                return change.ExecuteNonQuery();
            }));
            ChangeBoundOperation("completed", "accepted");
            try { Assert.Equal("extension_payload_history_unbound", Assert.Throws<IOException>(proof.RequireCurrent).Message); }
            finally { ChangeBoundOperation("accepted", "completed"); }
            Assert.Equal(original.PlanReference, proof.ReadCurrent().PlanReference);

            var settings = new HostSettingsStore(database);
            void SetEnabled(bool enabled)
            {
                var current = settings.Read();
                var change = new HostSettingsChange(current.Revision, current.Values with { Extensions = [new("fixture-plugin", enabled)] });
                Assert.True(settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change), change,
                    DateTimeOffset.UtcNow).IsNew);
            }
            SetEnabled(false);
            try { Assert.Equal("extension_installation_consumption_unavailable", Assert.Throws<IOException>(proof.RequireCurrent).Message); }
            finally { SetEnabled(true); }
            Assert.Equal(original.PlanReference, proof.ReadCurrent().PlanReference);
            Assert.Equal(physical, PhysicalObjectIdentity.Read(manifestPath));
            Assert.Equal(manifest, File.ReadAllBytes(manifestPath));
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory(DisplayName = "Nested Database Readers Observe New Commits And Keep The Original File Protected Until Its Owner Exits")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesNestedLifetime(bool instanceOwned)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "database-reads-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(root);
        var database = StateDatabase.CreateFresh(instance);
        database.Accept(Operation(), DateTimeOffset.UtcNow);
        var path = database.DatabasePath;
        var replacement = Path.Combine(root, "replacement.db");
        File.Copy(path, replacement);
        using (var candidateSql = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={replacement};Pooling=False"))
        using (var write = candidateSql.BeginTransaction(deferred: false))
        { OperationRows.Accept(candidateSql, write, Operation(), DateTimeOffset.UtcNow); write.Commit(); }
        var original = PhysicalObjectIdentity.Read(path);
        var candidate = PhysicalObjectIdentity.Read(replacement);
        using (var pool = instanceOwned ? new DatabaseConnectionPool(path) : null)
        {
            using (var outer = DatabaseReadScope.Hold(path))
            {
                using (DatabaseReadScope.Hold(path))
                {
                    Assert.Equal(1L, Read(path));
                    Assert.Equal("database_read_scope_order", Assert.Throws<InvalidOperationException>(() => outer.Dispose()).Message);
                }
                var denied = Record.Exception(() => File.Move(replacement, path, overwrite: true));
                Assert.True(denied is IOException or UnauthorizedAccessException, denied?.Message);
                Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
                Assert.Equal(original, PhysicalObjectIdentity.Read(path));
                Assert.Equal(candidate, PhysicalObjectIdentity.Read(replacement));
                Assert.Equal(1L, Read(path));
            }
            using (var writer = DatabaseConnections.Open(path, domains: DatabaseDomain.Runtime))
            using (var command = writer.CreateCommand())
            {
                Assert.Equal(2L, DatabaseConnections.Scalar(writer, "PRAGMA synchronous;"));
                using var rollback = writer.BeginTransaction(); command.Transaction = rollback;
                OperationRows.Accept(writer, rollback, Operation(), DateTimeOffset.UtcNow); rollback.Rollback();
                using var saved = writer.BeginTransaction(deferred: false);
                OperationRows.Accept(writer, saved, Operation(), DateTimeOffset.UtcNow);
                OperationRows.Accept(writer, saved, Operation(), DateTimeOffset.UtcNow); saved.Commit();
            }
            using (var reader = DatabaseConnections.Open(path, readOnly: true, domains: DatabaseDomain.Runtime))
            using (var command = reader.CreateCommand())
            {
                command.CommandText = "DELETE FROM main.operations;";
                Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            }
            using (DatabaseReadScope.Hold(path)) Assert.Equal(3L, Read(path));
            if (instanceOwned)
            {
                var denied = Record.Exception(() => File.Move(replacement, path, overwrite: true));
                Assert.True(denied is IOException or UnauthorizedAccessException, denied?.Message);
            }
        }
        File.Move(replacement, path, overwrite: true);
        Assert.Equal(candidate, PhysicalObjectIdentity.Read(path));
        using (DatabaseReadScope.Hold(path)) Assert.Equal(2L, Read(path));
        Directory.Move(root, root + "-moved");
    }

    private static OperationIdentity Operation() => new(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.command");

    private static long CacheBudget(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var aliases = new List<string>();
        using (var command = DatabaseConnections.Command(connection, transaction, "PRAGMA database_list;"))
        using (var rows = command.ExecuteReader())
            while (rows.Read()) if (rows.GetString(1) != "temp") aliases.Add(rows.GetString(1));
        return aliases.Sum(alias =>
        {
            using var command = DatabaseConnections.Command(connection, transaction, "PRAGMA " + alias + ".cache_size;");
            return -(long)command.ExecuteScalar()!;
        });
    }

    private static long Read(string path)
    {
        using var connection = DatabaseConnections.Open(path, readOnly: true, domains: DatabaseDomain.Runtime);
        return (long)DatabaseConnections.Scalar(connection, "SELECT count(*) FROM main.operations;");
    }

    private static long ReadValue(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT retention_days FROM system.settings_snapshots WHERE revision=0;";
        command.Transaction = transaction;
        return (long)command.ExecuteScalar()!;
    }
}
