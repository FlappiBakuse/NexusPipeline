using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class StartupConnectionTests
{
    [Fact(DisplayName = "New Readers See Committed Facts Without Crossing Runtime Instances")]
    public void ReadsCurrentFacts()
    {
        var (database, records) = RecordStoreFixture.NewStore();
        var (foreign, _) = RecordStoreFixture.NewStore();
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        using (DatabaseReadScope.Hold(database.DatabasePath))
        {
            Assert.Empty(records.ReadPage(new()).Items);
            records.AppendCompleted([record]);
            Assert.Equal(record, Assert.Single(records.ReadPage(new()).Items));
            Assert.Empty(new RunRecordStore(foreign).ReadPage(new()).Items);
        }
        Assert.Equal(record, Assert.Single(records.ReadPage(new()).Items));
    }
}
