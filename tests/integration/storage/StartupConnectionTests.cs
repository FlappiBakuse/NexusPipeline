using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class StartupConnectionTests
{
    [Fact(DisplayName = "New Readers See Committed Facts Without Crossing Runtime Instances")]
    public void ReadsCurrentFacts()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var (foreign, _) = RecordStoreFixture.NewStore();
        using (DatabaseReadScope.Hold(database.DatabasePath))
        {
            using (var first = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
                Assert.Equal(0L, DatabaseConnections.Scalar(first, "SELECT count(*) FROM operations;"));
            database.Accept(new OperationIdentity(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.command"), DateTimeOffset.UtcNow);
            using (var second = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
                Assert.Equal(1L, DatabaseConnections.Scalar(second, "SELECT count(*) FROM operations;"));
            using (var outside = DatabaseConnections.Open(foreign.DatabasePath, readOnly: true))
                Assert.Equal(0L, DatabaseConnections.Scalar(outside, "SELECT count(*) FROM operations;"));
        }
        using var after = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        Assert.Equal(1L, DatabaseConnections.Scalar(after, "SELECT count(*) FROM operations;"));
    }

    [Fact(DisplayName = "A Startup Database Name Is Pinned Until Its Pool Closes And Replacement Is Observed In The Next Scope")]
    public void PinsDatabaseIdentity()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var path = database.DatabasePath;
        var original = PhysicalObjectIdentity.Read(path);
        using (DatabaseReadScope.Hold(path))
        {
            using (DatabaseConnections.Open(path, readOnly: true)) { }
            Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".previous"));
        }
        File.Move(path, path + ".previous");
        File.Copy(path + ".previous", path);
        Assert.NotEqual(original, PhysicalObjectIdentity.Read(path));
        using (DatabaseReadScope.Hold(path))
        using (var current = DatabaseConnections.Open(path, readOnly: true))
            Assert.Equal(0L, DatabaseConnections.Scalar(current, "SELECT count(*) FROM operations;"));
        File.Delete(path);
        File.Move(path + ".previous", path);
        Assert.Equal(original, PhysicalObjectIdentity.Read(path));
    }
}
