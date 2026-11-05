using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class StartupConnectionTests
{
    [Fact(DisplayName = "Startup Reads Reuse Native Connections While Observing Current Committed Facts")]
    public void ReadsCurrentFacts()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        using var scope = DatabaseReadScope.Hold(database.DatabasePath);
        SQLitePCL.sqlite3 native;
        using (var first = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        {
            native = first.Handle!;
            Assert.True(new SqliteConnectionStringBuilder(first.ConnectionString).Pooling);
            Assert.Equal(0L, DatabaseConnections.Scalar(first, "SELECT count(*) FROM operations;"));
        }
        database.Accept(new OperationIdentity(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.command"), DateTimeOffset.UtcNow);
        using (var second = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        {
            Assert.Same(native, second.Handle);
            Assert.Equal(1L, DatabaseConnections.Scalar(second, "SELECT count(*) FROM operations;"));
        }
        using (var alias = DatabaseConnections.Open(database.DatabasePath.ToUpperInvariant(), readOnly: true))
            Assert.Same(native, alias.Handle);
        using (DatabaseReadScope.Hold(database.DatabasePath))
        using (DatabaseConnections.Open(database.DatabasePath, readOnly: true)) { }
        using (DatabaseConnections.Open(database.DatabasePath, readOnly: true)) { }
        using (var writer = DatabaseConnections.Open(database.DatabasePath))
            Assert.False(new SqliteConnectionStringBuilder(writer.ConnectionString).Pooling);
        var (foreign, _) = RecordStoreFixture.NewStore();
        using (var outside = DatabaseConnections.Open(foreign.DatabasePath, readOnly: true))
            Assert.False(new SqliteConnectionStringBuilder(outside.ConnectionString).Pooling);
        scope.Dispose();
        Assert.True(native.IsClosed);
        using var after = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        Assert.False(new SqliteConnectionStringBuilder(after.ConnectionString).Pooling);
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
