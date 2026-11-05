using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ImmutableBlobTests
{
    [Fact(DisplayName = "Physical Identity Survives Rename And Distinguishes Equal Byte Replacement")]
    public void TracksPhysicalIdentity()
    {
        var (root, _) = NewStore();
        var file = Path.Combine(root, "original.config");
        File.WriteAllText(file, "same bytes");
        var identity = PhysicalObjectIdentity.Read(file);
        var renamed = Path.Combine(root, "renamed.config");
        File.Move(file, renamed);
        Assert.Equal(identity, PhysicalObjectIdentity.Read(renamed));
        File.WriteAllText(file, "same bytes");
        Assert.NotEqual(identity, PhysicalObjectIdentity.Read(file));
        Assert.Equal(16, identity.VolumeSerial.Length);
        Assert.Equal(32, identity.FileId.Length);
    }

    [Fact(DisplayName = "Directory Identity Is Stable Across A Unicode Path Rename")]
    public void PreservesRenamedDirectories()
    {
        var (root, _) = NewStore();
        var directory = Path.Combine(root, "配置");
        Directory.CreateDirectory(directory);
        var identity = PhysicalObjectIdentity.Read(directory);
        var renamed = Path.Combine(root, "配置二");
        Directory.Move(directory, renamed);
        Assert.Equal(identity, PhysicalObjectIdentity.Read(renamed));
        Assert.NotEqual(identity, PhysicalObjectIdentity.Read(root));
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
        Assert.Equal(reference, store.PersistContent(source, frozen, CancellationToken.None));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "blobs", "content")));
        File.WriteAllBytes(source, [255, 2, 1, 0]);
        Assert.Throws<IOException>(() => store.PersistContent(source, frozen, CancellationToken.None));
        using var retained = store.OpenVerified(reference);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, ReadAll(retained));
        Assert.Throws<IOException>(() => File.WriteAllBytes(Path.Combine(root, reference.RelativePath), [7]));
    }

    [Fact(DisplayName = "Corrupted Content Addressed Bytes Are Preserved And Never Overwritten")]
    public void PreservesCorruptBlobs()
    {
        var (root, store) = NewStore();
        var reference = store.PersistPlan("{\"frozen\":true}"u8.ToArray());
        var path = Path.Combine(root, reference.RelativePath);
        File.WriteAllText(path, "unrecognized");
        var before = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => store.PersistPlan("{\"frozen\":true}"u8.ToArray()));
        Assert.Throws<IOException>(() => store.Verify(reference));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "Concurrent Verified Readers Hold Immutable Bytes Until The Last Reader Closes And Recheck Later Changes")]
    public async Task HoldsVerifiedReads()
    {
        var (root, store) = NewStore();
        var bytes = Enumerable.Range(0, 131072).Select(index => (byte)(index % 251)).ToArray();
        var reference = store.PersistContent(bytes, default);
        var path = Path.Combine(root, reference.RelativePath);
        var first = store.OpenVerified(reference);
        var second = store.OpenVerified(reference);
        try
        {
            first.SafeFileHandle.Dispose();
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, bytes));
            first.Dispose();
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => store.OpenVerified(reference with { SizeBytes = bytes.Length + 1 }));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                using var reader = store.OpenVerified(reference);
                Assert.Equal(bytes, ReadAll(reader));
            })));
            Assert.Equal(0, second.Position);
            Assert.Equal(bytes, ReadAll(second));
        }
        finally { first.Dispose(); await second.DisposeAsync(); }
        var changed = bytes.ToArray(); changed[0]++;
        File.WriteAllBytes(path, changed);
        Assert.Throws<IOException>(() => store.OpenVerified(reference));
        Assert.Equal(changed, File.ReadAllBytes(path));
        File.WriteAllBytes(path, bytes);
        using var restored = store.OpenVerified(reference);
        Assert.Equal(bytes, ReadAll(restored));
    }

    [Fact(DisplayName = "Invalid Plan And Cancelled Content Do Not Create Published Blob Files")]
    public void RejectsUnpublishedBlobs()
    {
        var (root, store) = NewStore();
        Assert.Throws<System.Text.Json.JsonException>(() => store.PersistPlan("{\"duplicate\":1,\"duplicate\":2}"u8.ToArray()));
        var source = Path.Combine(root, "fixture.config");
        File.WriteAllText(source, "fixture");
        var frozen = Assert.Single(new FileTreeScanner(SnapshotBudget.Default).Capture(source, CancellationToken.None).Entries);
        Assert.Throws<OperationCanceledException>(() => store.PersistContent(source, frozen, new CancellationToken(true)));
        Assert.False(Directory.Exists(Path.Combine(root, "blobs")));
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
