using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DatabaseCheckpointTests
{
    [Fact(DisplayName = "A Short Page Releases Its Snapshot And Wal Converges After An Observed Long Reader Ends")]
    public void ReleasesReadSnapshots()
    {
        var (database, records) = RecordStoreFixture.NewStore();
        records.AppendCompleted([RecordStoreFixture.Record(0, Guid.NewGuid())]);
        using var anchor = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
        anchor.Open();
        using (var transaction = anchor.BeginTransaction(deferred: true))
        {
            using var reader = anchor.CreateCommand();
            reader.Transaction = transaction;
            reader.CommandText = "SELECT count(*) FROM run_records;";
            Assert.Equal(1L, reader.ExecuteScalar());
            records.AppendCompleted(Enumerable.Range(1, 200).Select(number => RecordStoreFixture.Record(number, Guid.NewGuid())).ToArray());
            var pinned = database.Checkpoint();
            Assert.True(pinned.LogFrames > pinned.CheckpointedFrames);
            Assert.True(new FileInfo(database.DatabasePath + "-wal").Length > 0);
            transaction.Commit();
        }
        var page = records.ReadPage(new(50));
        Assert.Equal(50, page.Items.Count);
        var converged = database.Checkpoint(CheckpointMode.Truncate);
        Assert.Equal(0, converged.Busy);
        Assert.Equal(0, converged.LogFrames);
        Assert.True(!File.Exists(database.DatabasePath + "-wal") || new FileInfo(database.DatabasePath + "-wal").Length == 0);
    }
}
