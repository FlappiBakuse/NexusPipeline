using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class PhysicalPathTests
{
    [Theory(DisplayName = "Native Entry Names Preserve Case With Bounded Allocation And Long Paths")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsEntryNames(bool longPath)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "entry-name-" + Guid.NewGuid().ToString("N"));
        var parent = longPath ? Path.Combine(root, new string('a', 200), new string('b', 200), new string('c', 200)) : root;
        Directory.CreateDirectory(parent);
        File.WriteAllBytes(Path.Combine(parent, "Owned-文件.bin"), [1]);
        var path = Path.Combine(parent, "owned-文件.BIN");
        Assert.Equal("Owned-文件.bin", PhysicalObjectIdentity.EntryName(path));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 64; index++) _ = PhysicalObjectIdentity.EntryName(path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4 * 1024 * 1024, $"Entry name reads allocated {allocated} bytes");
    }

    [Fact(DisplayName = "Native Identity And Exact Deletion Work Beyond Max Path While Retaining An Unrelated Neighbor")]
    public void DeletesExactObjects()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "long-native-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, new string('a', 90), new string('b', 90), new string('c', 90));
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, "owned.bin");
        var neighbor = Path.Combine(parent, "neighbor.bin");
        byte[] bytes = [18, 0, 4];
        File.WriteAllBytes(path, bytes);
        File.WriteAllBytes(neighbor, [7, 8]);
        Assert.True(parent.Length > 260);
        var identity = PhysicalObjectIdentity.Read(path);
        var parentIdentity = PhysicalObjectIdentity.Read(parent);
        var intent = new FileObjectDeletion(path, parentIdentity, identity, FileObjectKind.File, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.False(PhysicalObjectDeletion.TryDeleteUnreferenced(intent, () => false));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.True(PhysicalObjectDeletion.TryDeleteUnreferenced(intent, () => true));
        Assert.False(File.Exists(path));
        Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(neighbor));
        Assert.Equal(parentIdentity, PhysicalObjectIdentity.Read(parent));
    }
}
