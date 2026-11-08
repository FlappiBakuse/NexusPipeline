using Microsoft.Data.Sqlite;
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
        long Read(StateDatabase database) => HistoryReadScope.ReadDatabase(database, (connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT revision FROM recovery_index_state WHERE singleton=1;";
            return (long)command.ExecuteScalar()!;
        });
        var initial = Read(a);
        using (var outer = new HistoryReadScope(first, a, contracts))
        {
            Assert.Equal(initial, Read(a));
            using (var inner = new HistoryReadScope(second, b, contracts))
            {
                Assert.Equal(initial, Read(b));
                Assert.Throws<InvalidOperationException>(() => outer.Dispose());
                a.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "UPDATE recovery_index_state SET revision=revision+1;";
                    return command.ExecuteNonQuery();
                });
                Assert.Equal(initial + 1, Read(a)); Assert.Equal(initial, Read(b));
            }
            Assert.Equal(initial + 1, Read(a));
            Assert.Throws<IOException>(() => File.Delete(a.DatabasePath));
        }
        Directory.Move(first.RuntimeRoot, first.RuntimeRoot + "-released");
        Directory.Move(second.RuntimeRoot, second.RuntimeRoot + "-released");
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
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT number FROM value;";
            if ((long)command.ExecuteScalar()! != 1) throw new IOException("changed_installation");
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
            long Observe() => held.Read((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT number FROM value;";
                return (long)command.ExecuteScalar()!;
            });
            Assert.Equal(1, Observe());
            held.RequireCurrent(Validate); held.RequireCurrent(Validate);
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
            Write(2);
            Assert.Equal(2, Observe());
            Assert.Throws<IOException>(() => held.RequireCurrent(Validate));
            Assert.Throws<IOException>(() => held.RequireCurrent(Validate));
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
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "SELECT number FROM value;";
                    Assert.Equal(3L, command.ExecuteScalar());
                    Write(2);
                });
                Assert.Throws<IOException>(() => held.RequireCurrent(Validate));
            }
        }
        Directory.Move(root, root + "-moved");
    }

    [Fact(DisplayName = "Nested Database Readers Keep The Original File Protected Until Their Outer Owner Exits And Reopen Replaced Bytes")]
    public void PreservesNestedLifetime()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "database-reads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "nexus.db");
        Create(path, 1);
        using (var outer = DatabaseReadScope.Hold(path))
        {
            using (DatabaseReadScope.Hold(path))
            {
                Assert.Equal(1L, Read(path));
                Assert.Throws<InvalidOperationException>(() => outer.Dispose());
            }
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
            Assert.Equal(1L, Read(path));
        }
        File.Delete(path);
        Create(path, 2);
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
        using var command = connection.CreateCommand(); command.CommandText = "SELECT number FROM value;";
        return (long)command.ExecuteScalar()!;
    }
}
