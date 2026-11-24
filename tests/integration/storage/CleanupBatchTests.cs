using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class CleanupBatchTests
{
    [Fact(DisplayName = "A Cleanup Page Protects Every Candidate And Releases Its Reference Snapshot Before A New Owner")]
    public void ProtectsPublicationLifetime()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var blobs = new ImmutableBlobStore(instance, new());
        var candidates = Enumerable.Range(0, 2).Select(index => blobs.PersistContent(BitConverter.GetBytes(index), default)).ToArray();
        var plan = new CleanupPlanCodec(instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), candidates);
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        new RunRecordStore(database).AppendCompleted([record]);
        var assets = new RecordAssetStore(database, blobs);
        RecordAsset Asset(BlobReference blob) => new(record.FinalAttempt.RunId, blob.Sha256, blob.SizeBytes, RecordAssetKind.Attachment);
        var cleanup = new BlobCleanup(instance, blobs, new(instance, new(), database, blobs));
        using (cleanup.ProtectCandidates(plan.Items))
        {
            Assert.Equal(candidates.OrderBy(blob => blob.Sha256, StringComparer.Ordinal),
                plan.Items.Select(item => item.Blob).OrderBy(blob => blob.Sha256, StringComparer.Ordinal));
            var inspection = cleanup.Inspect(plan.Items);
            Assert.Equal(plan.Items.Select(item => item.StepId).Order(StringComparer.Ordinal), inspection.Keys.Order(StringComparer.Ordinal));
            Assert.All(inspection.Values, state => Assert.Equal(CleanupItemState.VerifiedUnreferenced, state));
            foreach (var blob in candidates) Assert.Throws<IOException>(() => assets.AttachVerified(Asset(blob)));
        }
        foreach (var blob in candidates) assets.AttachVerified(Asset(blob));
        using (cleanup.ProtectCandidates(plan.Items))
            Assert.All(plan.Items, item => Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(item)));
        Assert.All(candidates, blob => Assert.True(File.Exists(Path.Combine(instance.RuntimeRoot, blob.RelativePath))));
    }

    [Fact(DisplayName = "A Candidate With An Existing Reader Continues To Recheck References Outside Page Protection")]
    public void RechecksUnprotectedCandidate()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var blobs = new ImmutableBlobStore(instance, new());
        var candidates = Enumerable.Range(0, 2).Select(index => blobs.PersistContent(BitConverter.GetBytes(index), default)).ToArray();
        var plan = new CleanupPlanCodec(instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), candidates);
        var selected = plan.Items[0];
        using var reader = blobs.OpenVerified(selected.Blob);
        var cleanup = new BlobCleanup(instance, blobs, new(instance, new(), database, blobs));
        using var protection = cleanup.ProtectCandidates(plan.Items);
        Assert.All(cleanup.Inspect(plan.Items).Values, state => Assert.Equal(CleanupItemState.VerifiedUnreferenced, state));
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        new RunRecordStore(database).AppendCompleted([record]);
        new RecordAssetStore(database, blobs).AttachVerified(new(record.FinalAttempt.RunId,
            selected.Blob.Sha256, selected.Blob.SizeBytes, RecordAssetKind.Attachment));
        reader.Dispose();
        Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(selected));
        Assert.True(File.Exists(Path.Combine(instance.RuntimeRoot, selected.Blob.RelativePath)));
        Assert.Equal(CleanupStepResult.Deleted, cleanup.RemoveVerified(plan.Items[1]));
    }

    [Fact(DisplayName = "A Protected Cleanup Page Reuses One Reference Inventory Through Every Exact Deletion")]
    public void SharesRemovalInventory()
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
        using var protection = cleanup.ProtectCandidates(plan.Items);
        Assert.All(cleanup.Inspect(plan.Items).Values, state => Assert.Equal(CleanupItemState.VerifiedUnreferenced, state));
        Assert.All(cleanup.Inspect(plan.Items).Values, state => Assert.Equal(CleanupItemState.VerifiedUnreferenced, state));
        foreach (var item in plan.Items) Assert.Equal(CleanupStepResult.Deleted, cleanup.RemoveVerified(item));
        Assert.Equal(1, reads);
        Assert.All(candidates, blob => Assert.False(File.Exists(Path.Combine(instance.RuntimeRoot, blob.RelativePath))));
    }

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
