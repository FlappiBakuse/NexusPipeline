using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class TreeSnapshotTests
{
    [Fact(DisplayName = "Missing Empty File And Empty Directory Remain Distinct")]
    public void DistinguishesEmptyObjects()
    {
        var root = NewRoot();
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        Assert.Equal(FileObjectKind.Missing, scanner.Capture(Path.Combine(root, "missing"), CancellationToken.None).Shape);
        var file = Path.Combine(root, "empty");
        File.WriteAllBytes(file, []);
        var snapshot = scanner.Capture(file, CancellationToken.None);
        Assert.Equal(FileObjectKind.File, snapshot.Shape);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Assert.Single(snapshot.Entries).Sha256);
        var directory = Path.Combine(root, "directory");
        Directory.CreateDirectory(directory);
        snapshot = scanner.Capture(directory, CancellationToken.None);
        Assert.Equal(FileObjectKind.Directory, snapshot.Shape);
        Assert.Equal(FileObjectKind.Directory, Assert.Single(snapshot.Entries).Kind);
    }

    [Fact(DisplayName = "Unicode Nested Paths Keep Exact Bytes And Content Digests")]
    public void PreservesUnicodeBytes()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "资料"));
        var file = Path.Combine(root, "资料", "配置.bin");
        File.WriteAllBytes(file, [97, 98, 99]);
        var snapshot = new FileTreeScanner(SnapshotBudget.Default).Capture(root, CancellationToken.None);
        var entry = Assert.Single(snapshot.Entries, entry => entry.Kind == FileObjectKind.File);
        Assert.Equal("资料/配置.bin", entry.RelativePath);
        Assert.Equal(3, entry.SizeBytes);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", entry.Sha256);
        Assert.Equal(new byte[] { 97, 98, 99 }, File.ReadAllBytes(file));
    }

    [Theory(DisplayName = "Snapshot Budgets Reject Oversize Input Without Changing It")]
    [InlineData(1, 3, "snapshot_entry_limit")]
    [InlineData(2, 2, "snapshot_byte_limit")]
    public void BoundsSnapshotInputs(int entries, int bytes, string reason)
    {
        var root = NewRoot();
        var file = Path.Combine(root, "data");
        File.WriteAllBytes(file, [1, 2, 3]);
        var scanner = new FileTreeScanner(new(entries, bytes));
        Assert.Equal(reason, Assert.Throws<IOException>(() => scanner.Capture(root, CancellationToken.None)).Message);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
        var accepted = new FileTreeScanner(new(2, 3)).Capture(root, CancellationToken.None);
        var captured = Assert.Single(accepted.Entries, entry => entry.RelativePath == "data");
        Assert.Equal(3, captured.SizeBytes);
    }

    [Fact(DisplayName = "An Open Writer Prevents Claiming A Stable Snapshot")]
    public void RejectsOpenWriters()
    {
        var root = NewRoot();
        var file = Path.Combine(root, "data");
        File.WriteAllBytes(file, [0, 0, 0]);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            Assert.Throws<IOException>(() => scanner.Capture(root, CancellationToken.None));
            writer.Write([97, 98, 99]);
        }
        var snapshot = scanner.Capture(root, CancellationToken.None);
        var captured = Assert.Single(snapshot.Entries, entry => entry.RelativePath == "data");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", captured.Sha256);
    }

    [Theory(DisplayName = "Live And Broken Junctions Are Rejected Without Following Their Targets")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsDirectoryLinks(bool broken)
    {
        var root = NewRoot();
        var selected = Path.Combine(root, "selected");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(selected, "linked");
        ControlledJunction.Create(link, outside);
        if (broken) Directory.Delete(outside);
        else File.WriteAllText(Path.Combine(outside, "evidence.txt"), "keep outside bytes");
        var error = Assert.Throws<IOException>(() => new FileTreeScanner(SnapshotBudget.Default).Capture(selected, CancellationToken.None));
        Assert.Equal("filesystem_reparse_point_rejected", error.Message);
        if (!broken) Assert.Equal("keep outside bytes", File.ReadAllText(Path.Combine(outside, "evidence.txt")));
    }

    private static string NewRoot()
    {
        var parent = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Explicit test root required");
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
