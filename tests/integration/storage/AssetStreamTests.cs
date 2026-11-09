using System.Security.Cryptography;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class AssetStreamTests
{
    [Fact(DisplayName = "A Slow Reader Streams The Original Large Asset And Pins Only Its Physical File")]
    public async Task StreamsPinnedAssets()
    {
        var fixture = Create();
        using var input = fixture.Store.OpenVerified(fixture.Asset);
        Assert.False(input.CanWrite); Assert.Equal(fixture.Asset.SizeBytes, input.Length);
        Assert.Throws<IOException>(() => File.Delete(fixture.Path));
        var records = new RunRecordStore(fixture.Database);
        var later = RecordStoreFixture.Record(3, Guid.NewGuid());
        records.AppendCompleted([later]);
        Assert.Contains(later, records.ReadPage(new()).Items);
        var buffer = new byte[65536]; long read = 0;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            var count = await input.ReadAsync(buffer);
            if (count == 0) break;
            digest.AppendData(buffer.AsSpan(0, count)); read += count;
        }
        Assert.Equal(fixture.Asset.SizeBytes, read);
        Assert.Equal(fixture.Asset.Sha256, Convert.ToHexStringLower(digest.GetHashAndReset()));
        input.Dispose();
        using var exclusive = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(fixture.Asset.SizeBytes, exclusive.Length);
    }

    [Theory(DisplayName = "An Asset Cannot Be Read Through An Unbound Metadata Relation Or Changed Content")]
    [InlineData("run")]
    [InlineData("size")]
    [InlineData("kind")]
    [InlineData("digest")]
    [InlineData("tampered")]
    [InlineData("missing")]
    [InlineData("instance")]
    public void RejectsUnboundReads(string fault)
    {
        var fixture = Create();
        var request = fault switch
        {
            "run" => fixture.Asset with { RunId = Guid.NewGuid() },
            "size" => fixture.Asset with { SizeBytes = fixture.Asset.SizeBytes + 1 },
            "kind" => fixture.Asset with { Kind = RecordAssetKind.Attachment },
            "digest" => fixture.Asset with { Sha256 = new string('c', 64) },
            _ => fixture.Asset
        };
        if (fault == "tampered") File.WriteAllText(fixture.Path, "unknown-content-preserve");
        if (fault == "missing") File.Delete(fixture.Path);
        var store = fixture.Store;
        if (fault == "instance")
        {
            var other = Create();
            store = new(fixture.Database, other.Blobs);
        }
        Assert.ThrowsAny<IOException>(() => { using var ignored = store.OpenVerified(request); });
        Assert.Equal(fixture.Asset, Assert.Single(fixture.Store.ReadForRun(fixture.Asset.RunId).Items));
        if (fault == "tampered") Assert.Equal("unknown-content-preserve", File.ReadAllText(fixture.Path));
    }

    private static (StateDatabase Database, ImmutableBlobStore Blobs, RecordAssetStore Store, RecordAsset Asset, string Path) Create()
    {
        var (database, records) = RecordStoreFixture.NewStore();
        var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(database.DatabasePath))!;
        var instance = new InstanceRoot(new()).OpenExisting(root);
        var blobs = new ImmutableBlobStore(instance, new());
        var file = System.IO.Path.Combine(root, "controlled-image.bin");
        var buffer = Enumerable.Range(0, 65536).Select(value => (byte)value).ToArray();
        using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            for (var index = 0; index < 128; index++) output.Write(buffer);
        var frozen = Assert.Single(new FileTreeScanner(SnapshotBudget.Default)
            .Capture(file, default).Entries);
        var reference = blobs.PersistContent(file, frozen, default);
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        records.AppendCompleted([record]);
        var asset = new RecordAsset(record.FinalAttempt.RunId, reference.Sha256, reference.SizeBytes, RecordAssetKind.Screenshot);
        var store = new RecordAssetStore(database, blobs); store.AttachVerified(asset);
        return (database, blobs, store, asset, System.IO.Path.Combine(root, reference.RelativePath));
    }
}
