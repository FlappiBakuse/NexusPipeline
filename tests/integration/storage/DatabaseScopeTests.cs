using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DatabaseScopeTests
{
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

    [Fact(DisplayName = "Nested Database Readers Keep The Original File Protected Until Their Outer Owner Exits And Reopen Replaced Bytes")]
    public void PreservesNestedLifetime()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "database-reads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "nexus.db");
        Create(path, 1);
        var replacement = Path.Combine(root, "replacement.db");
        Create(replacement, 2);
        var original = PhysicalObjectIdentity.Read(path);
        var candidate = PhysicalObjectIdentity.Read(replacement);
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
