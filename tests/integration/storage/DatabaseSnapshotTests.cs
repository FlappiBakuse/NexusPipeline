using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DatabaseSnapshotTests
{
    [Fact(DisplayName = "A Returned History Page Releases Its Snapshot Before The Next Atomic Save")]
    public void ReleasesReadSnapshots()
    {
        var (database, records) = RecordStoreFixture.NewStore();
        var automation = Guid.NewGuid();
        var original = RecordStoreFixture.Record(0, automation);
        records.AppendCompleted([original]);
        var first = records.ReadPage(new(50, automation));
        Assert.Equal(original, Assert.Single(first.Items));
        var added = Enumerable.Range(1, 200).Select(number => RecordStoreFixture.Record(number, automation) with
            { EndedAt = original.EndedAt.AddSeconds(number) }).ToArray();
        records.AppendCompleted(added);
        var query = new RecordQuery(50, automation);
        var expected = added.Prepend(original).Take(query.Limit).ToArray();
        var reopened = new RunRecordStore(database);
        var page = reopened.ReadPage(query);
        Assert.Equal(expected, page.Items);
        Assert.Equal(new RecordCursor(expected[^1].EndedAt, expected[^1].FinalAttempt.RunId), page.Next);
        Assert.Equal(original, Assert.Single(first.Items));
        Assert.Equal(added[^1], Assert.Single(reopened.ReadPage(query with { EndedFromInclusive = added[^1].EndedAt }).Items));
    }

    [Fact(DisplayName = "A Busy History Reader Cannot Leave A Partially Committed Save")]
    public void PreservesPinnedSnapshot()
    {
        var (database, records) = RecordStoreFixture.NewStore();
        var automation = Guid.NewGuid();
        var original = RecordStoreFixture.Record(0, automation);
        var added = RecordStoreFixture.Record(1, automation);
        records.AppendCompleted([original]);
        using (var reading = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Records))
        using (var snapshot = reading.BeginTransaction(deferred: true))
        {
            using var query = reading.CreateCommand(); query.Transaction = snapshot;
            query.CommandText = "SELECT count(*) FROM records.run_records;";
            Assert.Equal(1L, query.ExecuteScalar());
            Assert.Equal(5, Assert.Throws<SqliteException>(() => records.AppendCompleted([added])).SqliteErrorCode);
            Assert.Equal(1L, query.ExecuteScalar());
            snapshot.Commit();
        }
        Assert.Equal(original, Assert.Single(records.ReadPage(new(50, automation)).Items));
        records.AppendCompleted([added]);
        Assert.Equal(2, records.ReadPage(new(50, automation)).Items.Count);
    }
}
