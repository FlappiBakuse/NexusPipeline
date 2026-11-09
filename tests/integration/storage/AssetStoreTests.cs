using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class AssetStoreTests
{
    [Fact(DisplayName = "Verified Asset Relations Are Idempotent Shared And Page By Digest And Kind")]
    public void PagesSharedAssets()
    {
        var fixture = NewStore();
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        var second = RecordStoreFixture.Record(2, Guid.NewGuid());
        new RunRecordStore(fixture.Database).AppendCompleted([record, second]);
        var image = new RecordAsset(record.FinalAttempt.RunId, fixture.Blob.Sha256, fixture.Blob.SizeBytes, RecordAssetKind.Screenshot);
        fixture.Store.AttachVerified(image);
        fixture.Store.AttachVerified(image);
        fixture.Store.AttachVerified(image with { Kind = RecordAssetKind.Attachment });
        fixture.Store.AttachVerified(image with { RunId = second.FinalAttempt.RunId });
        var page = fixture.Store.ReadForRun(image.RunId, limit: 1);
        Assert.Equal(image, Assert.Single(page.Items));
        Assert.NotNull(page.Next);
        var final = fixture.Store.ReadForRun(image.RunId, page.Next, 1);
        Assert.Equal(RecordAssetKind.Attachment, Assert.Single(final.Items).Kind);
        Assert.Null(final.Next);
        Assert.Equal(image with { RunId = second.FinalAttempt.RunId },
            Assert.Single(fixture.Store.ReadForRun(second.FinalAttempt.RunId).Items));
    }

    [Theory(DisplayName = "Unverified Assets Cannot Publish Dangling Or Cross Instance Relations")]
    [InlineData("missing-record")]
    [InlineData("corrupt-blob")]
    [InlineData("locked-blob")]
    [InlineData("wrong-instance")]
    public void RejectsUnverifiedRelations(string fault)
    {
        var fixture = NewStore();
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        if (fault != "missing-record") new RunRecordStore(fixture.Database).AppendCompleted([record]);
        var path = Path.Combine(fixture.Root, fixture.Blob.RelativePath);
        FileStream? writer = null;
        try
        {
            if (fault == "corrupt-blob") File.WriteAllText(path, "unknown bytes preserve");
            if (fault == "locked-blob") writer = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var store = fault == "wrong-instance" ? new RecordAssetStore(fixture.Database, NewStore().Blobs) : fixture.Store;
            Action attach = () => store.AttachVerified(new(record.FinalAttempt.RunId, fixture.Blob.Sha256, fixture.Blob.SizeBytes, RecordAssetKind.Screenshot));
            if (fault == "missing-record") Assert.Throws<SqliteException>(attach);
            else Assert.Throws<IOException>(attach);
        }
        finally { writer?.Dispose(); }
        Assert.Empty(fixture.Store.ReadForRun(record.FinalAttempt.RunId).Items);
        if (fault == "corrupt-blob") Assert.Equal("unknown bytes preserve", File.ReadAllText(path));
    }

    private static (string Root, StateDatabase Database, ImmutableBlobStore Blobs, BlobReference Blob, RecordAssetStore Store) NewStore()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var root = Path.GetDirectoryName(Path.GetDirectoryName(database.DatabasePath))!;
        var instance = new InstanceRoot(new ContractReader()).OpenExisting(root);
        var blobs = new ImmutableBlobStore(instance, new ContractReader());
        var source = Path.Combine(root, "fixture-image.bin");
        File.WriteAllBytes(source, Enumerable.Range(0, 1024).Select(item => (byte)item).ToArray());
        var frozen = Assert.Single(new FileTreeScanner(SnapshotBudget.Default).Capture(source, CancellationToken.None).Entries);
        var blob = blobs.PersistContent(source, frozen, CancellationToken.None);
        return (root, database, blobs, blob, new(database, blobs));
    }
}
