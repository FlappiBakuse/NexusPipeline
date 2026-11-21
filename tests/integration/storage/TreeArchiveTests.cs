using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class TreeArchiveTests
{
    [Fact(DisplayName = "Protected Archives Read Each Source Once And Copy Only Content Missing From The Blob Store")]
    public void BoundsProtectedReads()
    {
        var (root, archive) = Create();
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var firstFile = Path.Combine(source, "first.bin");
        File.WriteAllBytes(firstFile, [1, 7, 3]);
        File.WriteAllBytes(Path.Combine(source, "second.bin"), [8, 2]);
        var parent = PhysicalObjectIdentity.Read(root);
        var stderr = Console.Error;
        using var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            using var profile = StartupProfile.Begin("protected-configuration-archive");
            using (var scene = archive.CaptureProtected(source, parent, default, out var cold))
            {
                Assert.Equal(new[] { "", "empty", "first.bin", "second.bin" }, cold.Snapshot.Entries.Select(entry => entry.RelativePath));
                Assert.Equal(new long[] { 3, 2 }, cold.Contents.Select(content => content.SizeBytes));
                Assert.Throws<IOException>(() => File.Open(firstFile, FileMode.Open, FileAccess.Write, FileShare.Read));
                var unlisted = Path.Combine(source, "empty", "unlisted");
                File.WriteAllText(unlisted, "external");
                Assert.Throws<IOException>(() => scene.RequireNamespace());
                File.Delete(unlisted);
            }
            using var hit = archive.CaptureProtected(source, parent, default, out var archived);
            Assert.Equal(2, archived.Contents.Count);
        }
        finally { Console.SetError(stderr); }
        using var report = JsonDocument.Parse(captured.ToString());
        var steps = report.RootElement.GetProperty("steps").EnumerateArray().ToDictionary(
            step => step.GetProperty("name").GetString()!, step => step.GetProperty("count").GetInt64());
        Assert.Equal(4, steps["configuration.capture.hash"]);
        Assert.Equal(2, steps["configuration.capture.copy"]);
    }

    [Theory(DisplayName = "Archived Configuration Reconstructs Exact Shape And Bytes At An Exclusive New Path")]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("directory")]
    public void ReconstructsExactTrees(string shape)
    {
        var (root, archive) = Create();
        var source = Path.Combine(root, "source");
        byte[] archived = [0, 255, 7];
        byte[] changed = [3, 4, 5];
        var input = shape == "file" ? source : Path.Combine(source, "嵌套", "配置.bin");
        if (shape == "directory")
        {
            Directory.CreateDirectory(Path.Combine(source, "嵌套", "空目录"));
            File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
            File.WriteAllBytes(Path.Combine(source, "duplicate.bin"), archived);
        }
        if (shape != "missing") File.WriteAllBytes(input, archived);
        var original = shape == "missing" ? null : PhysicalObjectIdentity.Read(source);
        var frozen = archive.Capture(source, CancellationToken.None);
        if (original is not null) File.WriteAllBytes(input, changed);

        var destination = Path.Combine(root, "candidate");
        var identity = archive.MaterializeNew(frozen, destination, CancellationToken.None);
        if (shape == "missing")
        {
            Assert.Null(identity);
            Assert.False(Path.Exists(destination));
            Assert.False(Path.Exists(source));
            return;
        }
        Assert.Equal(PhysicalObjectIdentity.Read(destination), identity);
        Assert.NotEqual(original, identity);
        Assert.Equal(original, PhysicalObjectIdentity.Read(source));
        Assert.Equal(changed, File.ReadAllBytes(input));
        if (shape == "file")
        {
            Assert.Equal(archived, File.ReadAllBytes(destination));
            return;
        }
        var paths = Directory.EnumerateFileSystemEntries(destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(destination, path).Replace('\\', '/')).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "duplicate.bin", "empty.bin", "嵌套", "嵌套/空目录", "嵌套/配置.bin" }.Order(StringComparer.Ordinal), paths);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(destination, "嵌套", "空目录")));
        Assert.Equal(archived, File.ReadAllBytes(Path.Combine(destination, "嵌套", "配置.bin")));
        Assert.Equal(archived, File.ReadAllBytes(Path.Combine(destination, "duplicate.bin")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(destination, "empty.bin")));
    }

    [Theory(DisplayName = "Existing Destination Bytes And Shape Are Never Replaced")]
    [InlineData("file")]
    [InlineData("directory")]
    public void PreservesExistingDestinations(string shape)
    {
        var (root, archive) = Create();
        var source = Path.Combine(root, "source");
        File.WriteAllText(source, "candidate");
        var destination = Path.Combine(root, "existing");
        var existing = shape == "file" ? destination : Path.Combine(destination, "unknown");
        if (shape == "directory") Directory.CreateDirectory(destination);
        File.WriteAllText(existing, "preserve");
        var original = PhysicalObjectIdentity.Read(destination);
        var content = PhysicalObjectIdentity.Read(existing);
        var frozen = archive.Capture(source, CancellationToken.None);
        Assert.Equal("file_tree_destination_not_empty", Assert.Throws<IOException>(() =>
            archive.MaterializeNew(frozen, destination, CancellationToken.None)).Message);
        Assert.Equal(original, PhysicalObjectIdentity.Read(destination));
        Assert.Equal(content, PhysicalObjectIdentity.Read(existing));
        Assert.Equal("preserve", File.ReadAllText(existing));
        if (shape == "directory") Assert.Equal(existing, Assert.Single(Directory.EnumerateFileSystemEntries(destination)));
    }

    [Fact(DisplayName = "Corrupt Archive Content Is Detected Before Creating Any Destination Object")]
    public void RejectsCorruptContent()
    {
        var (root, archive) = Create();
        var source = Path.Combine(root, "source");
        File.WriteAllText(source, "candidate");
        var frozen = archive.Capture(source, CancellationToken.None);
        File.WriteAllText(Path.Combine(root, "runtime", Assert.Single(frozen.Contents).RelativePath), "tampered!");
        var destination = Path.Combine(root, "candidate");
        Assert.Equal("immutable_blob_mismatch",
            Assert.Throws<IOException>(() => archive.MaterializeNew(frozen, destination, CancellationToken.None)).Message);
        Assert.False(Path.Exists(destination));
        Assert.Equal("candidate", File.ReadAllText(source));
    }

    [Theory(DisplayName = "Invalid Archive Paths Cannot Create Partial Objects Or Escape The Destination")]
    [InlineData("../outside", "unsupported_filesystem_component")]
    [InlineData("absent/child", "file_tree_archive_parent_missing")]
    public void RejectsInvalidPaths(string path, string reason)
    {
        var (root, archive) = Create();
        var entries = new[] { new FileTreeEntry("", FileObjectKind.Directory, 0, null),
            new FileTreeEntry(path, FileObjectKind.Directory, 0, null) };
        var frozen = new ArchivedFileTree(new(FileObjectKind.Directory, entries), [], []);
        var destination = Path.Combine(root, "candidate");
        Assert.Equal(reason, Assert.Throws<IOException>(() => archive.MaterializeNew(frozen, destination, CancellationToken.None)).Message);
        Assert.False(Path.Exists(destination));
        Assert.False(Path.Exists(Path.Combine(root, "outside")));
    }

    private static (string Root, FileTreeArchive Archive) Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        return (root, new(scanner, new ImmutableBlobStore(instance, contracts)));
    }
}
