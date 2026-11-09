using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DatabaseCheckpointTests
{
    [Fact(DisplayName = "A Short Page Releases Its Snapshot And Wal Converges After An Observed Long Reader Ends")]
    public void ReleasesReadSnapshots()
    {
        var (database, records) = RecordStoreFixture.NewStore();
        var automation = Guid.NewGuid();
        var original = RecordStoreFixture.Record(0, automation);
        records.AppendCompleted([original]);
        var added = Enumerable.Range(1, 200).Select(number => RecordStoreFixture.Record(number, automation) with
            { EndedAt = original.EndedAt.AddSeconds(number) }).ToArray();
        var query = new RecordQuery(50, automation);
        var firstPage = added.Prepend(original).Take(query.Limit).ToArray();
        using (var anchor = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = anchor.BeginTransaction(deferred: true))
        {
            AssertOldRecord(anchor, transaction, query, original);
            records.AppendCompleted(added);
            AssertOldRecord(anchor, transaction, query, original);
            var page = records.ReadPage(query);
            Assert.Equal(firstPage, page.Items);
            Assert.Equal(new RecordCursor(firstPage[^1].EndedAt, firstPage[^1].FinalAttempt.RunId), page.Next);
            var pinned = database.Checkpoint();
            Assert.True(pinned.LogFrames > pinned.CheckpointedFrames);
            Assert.True(new FileInfo(database.DatabasePath + "-wal").Length > 0);
            transaction.Commit();
        }
        var converged = database.Checkpoint(CheckpointMode.Truncate);
        Assert.Equal(0, converged.Busy);
        Assert.True(!File.Exists(database.DatabasePath + "-wal") || new FileInfo(database.DatabasePath + "-wal").Length == 0);
        var reopened = new RunRecordStore(database);
        Assert.Equal(firstPage, reopened.ReadPage(query).Items);
        Assert.Equal(added[^1], Assert.Single(reopened.ReadPage(query with { EndedFromInclusive = added[^1].EndedAt }).Items));
    }

    private static void AssertOldRecord(SqliteConnection connection, SqliteTransaction transaction, RecordQuery query, CompletedRunRecord expected)
    {
        using var command = RecordPageQuery.Create(connection, query);
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expected, RunRecordStore.ReadRecord(reader));
        Assert.False(reader.Read());
    }
}
