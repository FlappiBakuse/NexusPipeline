using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class CleanupBatchTests
{
    [Fact(DisplayName = "A Cleanup Batch Inspects Every Candidate With One Reference Inventory")]
    public void SharesReferenceInventory()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var blobs = new ImmutableBlobStore(instance, new());
        var candidates = Enumerable.Range(0, 128).Select(index => blobs.PersistContent(BitConverter.GetBytes(index), default)).ToArray();
        var plan = new CleanupPlanCodec(instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), candidates);
        var reads = 0;
        var references = new BlobReferenceIndex(instance, new(), database, blobs) { InventoryRead = () => reads++ };
        var cleanup = new BlobCleanup(instance, blobs, references);
        var inspected = cleanup.Inspect(plan.Items);
        Assert.Equal(candidates.Length, inspected.Count);
        Assert.All(plan.Items, item => Assert.Equal(CleanupItemState.VerifiedUnreferenced, inspected[item.StepId]));
        Assert.Equal(1, reads);
    }
}
