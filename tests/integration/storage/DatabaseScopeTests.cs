using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DatabaseScopeTests
{
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
            using (var connection = DatabaseConnections.Open(database.DatabasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE recovery_index_state SET revision=revision+1 WHERE singleton=1;";
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

    [Theory(DisplayName = "Protected Installation Reads Observe External Commits And Retain No Failed Validation Or Database Lock")]
    [InlineData("DELETE")]
    [InlineData("WAL")]
    public void RechecksCommittedChanges(string mode)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "database-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "nexus.db"); Create(path, 1);
        using (var setup = DatabaseConnections.Open(path))
            _ = DatabaseConnections.Scalar(setup, "PRAGMA journal_mode=" + mode + ";");
        void Validate(SqliteConnection connection, SqliteTransaction transaction)
        {
            if (ReadValue(connection, transaction) != 1) throw new IOException("changed_installation");
        }
        void Write(long value)
        {
            using var connection = DatabaseConnections.Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE value SET number=$number;";
            command.Parameters.AddWithValue("$number", value); command.ExecuteNonQuery();
        }
        using (var held = new DatabaseValidationLease(path))
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
                command.CommandText = "UPDATE value SET number=9;";
                return command.ExecuteNonQuery();
            }));
            Assert.Equal(1, Observe());
            if (mode == "WAL")
            {
                Write(3);
                held.RequireCurrent((connection, transaction) =>
                {
                    Assert.Equal(3L, ReadValue(connection, transaction));
                    Write(2);
                    Assert.Equal(3L, ReadValue(connection, transaction));
                });
                Assert.Equal("changed_installation", Assert.Throws<IOException>(() => held.RequireCurrent(Validate)).Message);
                Assert.Equal(2, Observe());
            }
        }
        var moved = root + "-moved";
        Directory.Move(root, moved);
        Assert.Equal(mode == "WAL" ? 2 : 1, Read(Path.Combine(moved, "nexus.db")));
    }

    [Theory(DisplayName = "Nested Database Readers Observe New Commits And Keep The Original File Protected Until Its Owner Exits")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesNestedLifetime(bool instanceOwned)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "database-reads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "nexus.db");
        Create(path, 1);
        var replacement = Path.Combine(root, "replacement.db");
        Create(replacement, 2);
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
            using (var writer = DatabaseConnections.Open(path))
            using (var command = writer.CreateCommand())
            {
                Assert.Equal(2L, DatabaseConnections.Scalar(writer, "PRAGMA synchronous;"));
                using var rollback = writer.BeginTransaction(); command.Transaction = rollback;
                command.CommandText = "UPDATE value SET number=9;"; command.ExecuteNonQuery(); rollback.Rollback();
                command.Transaction = null; command.CommandText = "UPDATE value SET number=3;"; command.ExecuteNonQuery();
            }
            using (var reader = DatabaseConnections.Open(path, readOnly: true))
            using (var command = reader.CreateCommand())
            {
                command.CommandText = "UPDATE value SET number=7;";
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

    private static void Create(string path, long value)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE value(number INTEGER); INSERT INTO value VALUES($number);";
        command.Parameters.AddWithValue("$number", value); command.ExecuteNonQuery();
    }

    private static long Read(string path)
    {
        using var connection = DatabaseConnections.Open(path, readOnly: true);
        return ReadValue(connection, null);
    }

    private static long ReadValue(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT number FROM value;";
        command.Transaction = transaction;
        return (long)command.ExecuteScalar()!;
    }
}
