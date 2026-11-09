using System.Security.Cryptography;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ImmutableBlobTests
{
    [Fact(DisplayName = "A Renamed Unicode Directory Keeps Its Identity And A Replacement Has Its Own")]
    public void PreservesRenamedDirectories()
    {
        var (root, _) = NewStore();
        var directory = Path.Combine(root, "配置");
        Directory.CreateDirectory(directory);
        var identity = PhysicalObjectIdentity.Read(directory);
        var renamed = Path.Combine(root, "配置二");
        Directory.Move(directory, renamed);
        Directory.CreateDirectory(directory);
        Assert.Equal(identity, PhysicalObjectIdentity.Read(renamed));
        Assert.NotEqual(identity, PhysicalObjectIdentity.Read(directory));
    }

    [Fact(DisplayName = "Frozen Content Is Deduplicated And Remains Independent Of Later Source Writes")]
    public void OwnsDeduplicatedContent()
    {
        var (root, store) = NewStore();
        var source = Path.Combine(root, "fixture.config");
        File.WriteAllBytes(source, [0, 1, 2, 255]);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var frozen = Assert.Single(scanner.Capture(source, CancellationToken.None).Entries);
        var reference = store.PersistContent(source, frozen, CancellationToken.None);
        var path = Path.Combine(root, reference.RelativePath);
        var identity = PhysicalObjectIdentity.Read(path);
        Assert.Equal(reference, store.PersistContent(source, frozen, CancellationToken.None));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        File.WriteAllBytes(source, [255, 2, 1, 0]);
        Assert.Equal("blob_source_changed",
            Assert.Throws<IOException>(() => store.PersistContent(source, frozen, CancellationToken.None)).Message);
        using var retained = store.OpenVerified(reference);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, ReadAll(retained));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
    }

    [Fact(DisplayName = "Corrupted Content Addressed Bytes Are Preserved And Never Overwritten")]
    public void PreservesCorruptBlobs()
    {
        var (root, store) = NewStore();
        var reference = store.PersistPlan("{\"frozen\":true}"u8.ToArray());
        var path = Path.Combine(root, reference.RelativePath);
        File.WriteAllText(path, "{\"frozen\":null}");
        var before = File.ReadAllBytes(path);
        var identity = PhysicalObjectIdentity.Read(path);
        Assert.Equal("immutable_blob_mismatch", Assert.Throws<IOException>(() => store.PersistPlan("{\"frozen\":true}"u8.ToArray())).Message);
        Assert.Equal("immutable_blob_mismatch", Assert.Throws<IOException>(() => store.Verify(reference)).Message);
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "Concurrent Verified Readers Hold Immutable Bytes Until The Last Reader Closes And Recheck Later Changes")]
    public async Task HoldsVerifiedReads()
    {
        var (root, store) = NewStore();
        var bytes = Enumerable.Range(0, 131072).Select(index => (byte)(index % 251)).ToArray();
        var reference = store.PersistContent(bytes, default);
        var path = Path.Combine(root, reference.RelativePath);
        using var first = store.OpenVerified(reference);
        Assert.Equal(bytes, ReadAll(first));
        first.SafeFileHandle.Dispose();
        Assert.Throws<IOException>(() => File.WriteAllBytes(path, bytes));
        await using (var second = store.OpenVerified(reference))
        {
            first.Dispose();
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Equal("immutable_blob_mismatch",
                Assert.Throws<IOException>(() => store.OpenVerified(reference with { SizeBytes = bytes.Length + 1 })).Message);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                using var reader = store.OpenVerified(reference);
                Assert.Equal(bytes, ReadAll(reader));
            })));
            Assert.Equal(bytes, ReadAll(second));
        }
        var changed = bytes.ToArray(); changed[0]++;
        File.WriteAllBytes(path, changed);
        Assert.Equal("immutable_blob_mismatch", Assert.Throws<IOException>(() => store.OpenVerified(reference)).Message);
        Assert.Equal(changed, File.ReadAllBytes(path));
        File.WriteAllBytes(path, bytes);
        using var restored = store.OpenVerified(reference);
        Assert.Equal(bytes, ReadAll(restored));
    }

    [Fact(DisplayName = "Invalid Plan And Cancelled Content Do Not Create Published Blob Files")]
    public void RejectsUnpublishedBlobs()
    {
        var (root, store) = NewStore();
        var malformed = "{\"duplicate\":1,\"duplicate\":2}"u8.ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(malformed));
        Assert.Equal("duplicate_json_member", Assert.Throws<System.Text.Json.JsonException>(() => store.PersistPlan(malformed)).Message);
        Assert.False(Path.Exists(Path.Combine(root, "blobs", "plans", $"{digest}.json")));
        var source = Path.Combine(root, "fixture.config");
        File.WriteAllText(source, "fixture");
        var frozen = Assert.Single(new FileTreeScanner(SnapshotBudget.Default).Capture(source, CancellationToken.None).Entries);
        Assert.Throws<OperationCanceledException>(() => store.PersistContent(source, frozen, new CancellationToken(true)));
        Assert.False(Path.Exists(Path.Combine(root, "blobs", "content", $"{frozen.Sha256}.bin")));
    }

    private static (string Root, ImmutableBlobStore Store) NewStore()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(root);
        return (root, new(instance, contracts));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var result = new MemoryStream();
        stream.CopyTo(result);
        return result.ToArray();
    }
}
