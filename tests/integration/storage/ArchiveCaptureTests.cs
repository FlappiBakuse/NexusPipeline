using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ArchiveCaptureTests
{
    [Theory]
    [InlineData(4096)]
    [InlineData(1048576)]
    public void BoundsScanBytes(int size)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "one"), new byte[size]);
        File.WriteAllBytes(Path.Combine(source, "two"), Enumerable.Repeat((byte)17, size).ToArray());
        long scanned = 0;
        var scanner = new FileTreeScanner(SnapshotBudget.Default) { HashBytes = count => scanned += count };
        var archive = new FileTreeArchive(scanner, new(instance, contracts));
        var frozen = archive.Capture(source, default);
        Assert.Equal(4L * size, scanned);
        var captured = scanned;
        Assert.Equal(frozen.Snapshot.Entries, scanner.Capture(source, default).Entries);
        Assert.Equal(4L * size, scanned - captured);
        var destination = Path.Combine(root, "copy");
        archive.MaterializeNew(frozen, destination, default);
        Assert.Equal(frozen.Snapshot.Entries, scanner.Capture(destination, default).Entries);
        File.WriteAllText(Path.Combine(instance.RuntimeRoot, frozen.Contents[0].RelativePath), "changed after release");
        Assert.Throws<IOException>(() => archive.MaterializeNew(frozen, Path.Combine(root, "rejected"), default));
        Console.WriteLine($"treeBytes={2L * size}; archiveScanBytes={captured}; previousScanBytes={8L * size}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsChangedSources(bool resize)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(root);
        var source = Path.Combine(root, "source");
        File.WriteAllBytes(source, [1, 2, 3]);
        var frozen = Assert.Single(new FileTreeScanner(SnapshotBudget.Default).Capture(source, default).Entries);
        File.WriteAllBytes(source, resize ? [1, 2, 3, 4] : [4, 5, 6]);
        var blobs = new ImmutableBlobStore(instance, new());
        Assert.Throws<IOException>(() => blobs.PersistContent(source, frozen, default));
        Assert.Empty(Directory.Exists(Path.Combine(root, "blobs", "content"))
            ? Directory.GetFiles(Path.Combine(root, "blobs", "content")) : []);
    }
}
