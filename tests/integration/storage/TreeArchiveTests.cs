using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class TreeArchiveTests
{
    [Theory(DisplayName = "Archived Configuration Reconstructs Exact Shape And Bytes At An Exclusive New Path")]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("directory")]
    public void ReconstructsExactTrees(string shape)
    {
        var (root, archive, scanner) = Create();
        var source = Path.Combine(root, "source");
        if (shape == "file") File.WriteAllBytes(source, [0, 255, 7]);
        if (shape == "directory")
        {
            Directory.CreateDirectory(Path.Combine(source, "嵌套", "空目录"));
            File.WriteAllBytes(Path.Combine(source, "嵌套", "配置.bin"), [0, 255, 7]);
            File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
            File.WriteAllBytes(Path.Combine(source, "duplicate.bin"), [0, 255, 7]);
        }
        var frozen = archive.Capture(source, CancellationToken.None);
        var before = scanner.Capture(source, CancellationToken.None);
        var destination = Path.Combine(root, "candidate");
        var identity = archive.MaterializeNew(frozen, destination, CancellationToken.None);
        var result = scanner.Capture(destination, CancellationToken.None);
        Assert.Equal(before.Shape, result.Shape);
        Assert.Equal(before.Entries, result.Entries);
        if (shape == "missing") Assert.Null(identity);
        else Assert.NotEqual(frozen.OriginalIdentity, identity);
        Assert.Equal(before.Entries, scanner.Capture(source, CancellationToken.None).Entries);
        if (shape == "directory") Assert.Equal(2, frozen.Contents.Count);
    }

    [Theory(DisplayName = "Existing Destination Bytes And Shape Are Never Replaced")]
    [InlineData("file")]
    [InlineData("directory")]
    public void PreservesExistingDestinations(string shape)
    {
        var (root, archive, scanner) = Create();
        var source = Path.Combine(root, "source");
        File.WriteAllText(source, "candidate");
        var destination = Path.Combine(root, "existing");
        if (shape == "file") File.WriteAllText(destination, "preserve");
        else
        {
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "unknown"), "preserve");
        }
        var before = scanner.Capture(destination, CancellationToken.None);
        Assert.Throws<IOException>(() => archive.MaterializeNew(archive.Capture(source, CancellationToken.None), destination, CancellationToken.None));
        var after = scanner.Capture(destination, CancellationToken.None);
        Assert.Equal(before.Shape, after.Shape);
        Assert.Equal(before.Entries, after.Entries);
    }

    [Fact(DisplayName = "Corrupt Archive Content Is Detected Before Creating Any Destination Object")]
    public void RejectsCorruptContent()
    {
        var (root, archive, _) = Create();
        var source = Path.Combine(root, "source");
        File.WriteAllText(source, "candidate");
        var frozen = archive.Capture(source, CancellationToken.None);
        File.WriteAllText(Path.Combine(root, "runtime", Assert.Single(frozen.Contents).RelativePath), "corrupt");
        var destination = Path.Combine(root, "candidate");
        Assert.Throws<IOException>(() => archive.MaterializeNew(frozen, destination, CancellationToken.None));
        Assert.False(File.Exists(destination));
        Assert.False(Directory.Exists(destination));
        Assert.Equal("candidate", File.ReadAllText(source));
    }

    [Theory(DisplayName = "Invalid Archive Paths Cannot Create Partial Objects Or Escape The Destination")]
    [InlineData("escape")]
    [InlineData("missing-parent")]
    public void RejectsInvalidPaths(string fault)
    {
        var (root, archive, _) = Create();
        var entries = new[] { new FileTreeEntry("", FileObjectKind.Directory, 0, null),
            new FileTreeEntry(fault == "escape" ? "../outside" : "absent/child", FileObjectKind.Directory, 0, null) };
        var frozen = new ArchivedFileTree(new(FileObjectKind.Directory, entries), [], []);
        var destination = Path.Combine(root, "candidate");
        Assert.Throws<IOException>(() => archive.MaterializeNew(frozen, destination, CancellationToken.None));
        Assert.False(Directory.Exists(destination));
        Assert.False(Directory.Exists(Path.Combine(root, "outside")));
    }

    private static (string Root, FileTreeArchive Archive, FileTreeScanner Scanner) Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        return (root, new(scanner, new ImmutableBlobStore(instance, contracts)), scanner);
    }
}
