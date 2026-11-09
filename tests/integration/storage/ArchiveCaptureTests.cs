using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ArchiveCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsChangedSources(bool resize)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(root);
        var source = Path.Combine(root, "source");
        byte[] original = [1, 2, 3];
        byte[] changed = resize ? [1, 2, 3, 4] : [4, 5, 6];
        File.WriteAllBytes(source, original);
        var frozen = Assert.Single(new FileTreeScanner(SnapshotBudget.Default).Capture(source, default).Entries);
        File.WriteAllBytes(source, changed);
        var blobs = new ImmutableBlobStore(instance, contracts);
        Assert.Equal("blob_source_changed", Assert.Throws<IOException>(() => blobs.PersistContent(source, frozen, default)).Message);
        var candidate = Path.Combine(root, "blobs", "content", $"{frozen.Sha256}.bin");
        Assert.False(Path.Exists(candidate));
        Assert.Equal(changed, File.ReadAllBytes(source));

        File.WriteAllBytes(source, original);
        var published = blobs.PersistContent(source, frozen, default);
        Assert.Equal(candidate, Path.GetFullPath(Path.Combine(root, published.RelativePath)));
        Assert.Equal(original, File.ReadAllBytes(candidate));
    }
}
