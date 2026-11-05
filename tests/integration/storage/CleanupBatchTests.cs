using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class CleanupBatchTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(128)]
    public void SharesReferenceInventory(int count)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var blobs = new ImmutableBlobStore(instance, new());
        var candidates = Enumerable.Range(0, count).Select(index => blobs.PersistContent(BitConverter.GetBytes(index), default)).ToArray();
        var plan = new CleanupPlanCodec(instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), candidates);
        var reads = 0;
        var references = new BlobReferenceIndex(instance, new(), database, blobs) { InventoryRead = () => reads++ };
        var cleanup = new BlobCleanup(instance, blobs, references);
        Assert.All(cleanup.Inspect(plan.Items).Values, value => Assert.Equal(CleanupItemState.VerifiedUnreferenced, value));
        Assert.Equal(1, reads);
        var first = plan.Items[0];
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        new RunRecordStore(database).AppendCompleted([record]);
        new RecordAssetStore(database, blobs).AttachVerified(new(record.FinalAttempt.RunId,
            first.Blob.Sha256, first.Blob.SizeBytes, RecordAssetKind.Screenshot));
        Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(first));
        Assert.Equal(2, reads);
        blobs.Verify(first.Blob);
        Console.WriteLine($"candidates={count}; inspectionInventories=1; deletionFreshInventories=1");
    }
}
