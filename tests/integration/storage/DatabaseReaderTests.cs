using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DatabaseReaderTests
{
    [Fact(DisplayName = "Synchronous Read Scope Release Preserves The Instance Background Reader Pool")]
    public async Task PreservesBackgroundPool()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "reader-owner-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(root);
        var lease = InstanceLease.Acquire(instance, new());
        try
        {
            var database = StateDatabase.CreateFresh(instance, lease);
            object? background;
            using (var reader = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            {
                Assert.Equal(0L, Count(reader));
                background = reader.Handle;
            }
            lease.PublishExecution(() =>
            {
                using var reader = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
                Assert.Equal(0L, Count(reader));
            });
            using var following = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            Assert.Equal(0L, Count(following));
            Assert.Same(background, following.Handle);
        }
        finally
        {
            lease.Dispose();
            await lease.WhenExecutionPublicationsReleased;
        }
    }

    [Fact(DisplayName = "Instance Readers Observe Every New Commit And Remain Isolated Across Protected Database Lifetimes")]
    public async Task ObservesCurrentCommits()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "instance-readers-" + Guid.NewGuid().ToString("N"));
        var first = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "first"));
        var second = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "second"));
        var firstLease = InstanceLease.Acquire(first, new());
        var secondLease = InstanceLease.Acquire(second, new());
        var readers = new List<SqliteConnection>();
        try
        {
            var a = StateDatabase.CreateFresh(first, firstLease);
            var b = StateDatabase.CreateFresh(second, secondLease);
            var accepted = new OperationIdentity(Guid.NewGuid(), "reader-fixture", new string('a', 64), "fixture.reader");
            Assert.True(a.Accept(accepted, DateTimeOffset.UtcNow).IsNew);
            for (var index = 0; index < 12; index++) readers.Add(DatabaseConnections.Open(a.DatabasePath, readOnly: true));
            Assert.All(readers, connection => Assert.Equal(1L, Count(connection)));
            using (var other = DatabaseConnections.Open(b.DatabasePath, readOnly: true)) Assert.Equal(0L, Count(other));
            var following = new OperationIdentity(Guid.NewGuid(), "reader-fixture", new string('b', 64), "fixture.reader");
            Assert.True(a.Accept(following, DateTimeOffset.UtcNow).IsNew);
            Assert.All(readers, connection => Assert.Equal(2L, Count(connection)));
            firstLease.PublishExecution(() =>
            {
                using var inside = DatabaseConnections.Open(a.DatabasePath, readOnly: true);
                Assert.Equal(2L, Count(inside));
                Assert.False(a.Accept(following, DateTimeOffset.UtcNow).IsNew);
                Assert.Equal(2L, Count(inside));
            });
            firstLease.Dispose();
            await firstLease.WhenExecutionPublicationsReleased;
            Assert.Throws<IOException>(() => Directory.Move(first.RuntimeRoot, first.RuntimeRoot + "-released"));
            Assert.All(readers, connection => Assert.Equal(2L, Count(connection)));
            foreach (var reader in readers) reader.Dispose();
            readers.Clear();
            Directory.Move(first.RuntimeRoot, first.RuntimeRoot + "-released");
            using (var other = DatabaseConnections.Open(b.DatabasePath, readOnly: true)) Assert.Equal(0L, Count(other));
            secondLease.Dispose();
            await secondLease.WhenExecutionPublicationsReleased;
            Directory.Move(second.RuntimeRoot, second.RuntimeRoot + "-released");
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
            firstLease.Dispose(); secondLease.Dispose();
            await firstLease.WhenExecutionPublicationsReleased;
            await secondLease.WhenExecutionPublicationsReleased;
        }
    }

    private static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operations;";
        return (long)command.ExecuteScalar()!;
    }
}
