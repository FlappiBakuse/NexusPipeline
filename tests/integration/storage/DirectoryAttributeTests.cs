using System.ComponentModel;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DirectoryAttributeTests
{
    [Fact(DisplayName = "Retained Ancestor Names Cannot Move Until The Synchronous Attribute Scope Ends")]
    public void PinsDirectoryNames()
    {
        var instance = Create();
        var moved = instance.RuntimeRoot + "-moved";
        using (DirectoryAttributeScope.Hold(instance))
            Assert.Throws<IOException>(() => Directory.Move(instance.RuntimeRoot, moved));
        Directory.Move(instance.RuntimeRoot, moved);
        Assert.True(Directory.Exists(moved));
    }

    [Fact(DisplayName = "Retained Directory Attributes Never Cache Or Lock The Contents Of Their Child Files")]
    public void ReadsCurrentFiles()
    {
        var instance = Create();
        var path = Path.Combine(instance.RuntimeRoot, "blobs", "plans", "input.txt");
        File.WriteAllText(path, "before");
        using var scope = DirectoryAttributeScope.Hold(instance);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var before = scanner.Capture(path, default);
        File.WriteAllText(path, "after bytes");
        var after = scanner.Capture(path, default);
        Assert.NotEqual(before.Entries.Single().Sha256, after.Entries.Single().Sha256);
        Assert.Equal("after bytes", File.ReadAllText(path));
    }

    [Theory(DisplayName = "A Retained Parent Still Rejects Newly Introduced Live Or Broken Descendant Junctions")]
    [InlineData(false)]
    [InlineData(true)]
    public void RechecksDescendantLinks(bool broken)
    {
        var instance = Create();
        var parent = Path.Combine(instance.RuntimeRoot, "journals");
        var linked = Path.Combine(parent, "introduced");
        var outside = Path.Combine(instance.RuntimeRoot, "outside");
        Directory.CreateDirectory(parent); Directory.CreateDirectory(outside);
        var path = Path.Combine(linked, "uncreated", "evidence.txt");
        using var scope = DirectoryAttributeScope.Hold(instance);
        Assert.Equal(path, PhysicalPathPolicy.NormalizeWithoutLinks(path));
        ControlledJunction.Create(linked, outside);
        try
        {
            if (broken) Directory.Delete(outside);
            Assert.Equal("filesystem_reparse_point_rejected",
                Assert.Throws<IOException>(() => PhysicalPathPolicy.NormalizeWithoutLinks(path)).Message);
        }
        finally { Directory.Delete(linked); }
    }

    [Fact(DisplayName = "A Retained Directory Uses Fresh Native Reparse Attributes Instead Of Its Initial Attribute Value")]
    public void RechecksRetainedTags()
    {
        var instance = Create();
        var selected = Path.Combine(instance.RuntimeRoot, "blobs", "plans");
        var outside = Path.Combine(instance.RuntimeRoot, "outside");
        Directory.CreateDirectory(outside);
        var path = Path.Combine(selected, "missing.txt");
        var attached = false;
        try
        {
            using (DirectoryAttributeScope.Hold(instance))
            {
                Assert.Equal(path, PhysicalPathPolicy.NormalizeWithoutLinks(path));
                try { ControlledJunction.Attach(selected, outside); attached = true; }
                catch (Win32Exception error) when (error.NativeErrorCode == 32) { }
                if (attached)
                    Assert.Equal("filesystem_reparse_point_rejected",
                        Assert.Throws<IOException>(() => PhysicalPathPolicy.NormalizeWithoutLinks(path)).Message);
                else Assert.Equal(path, PhysicalPathPolicy.NormalizeWithoutLinks(path));
            }
            if (!attached) ControlledJunction.Attach(selected, outside);
            Assert.Equal("filesystem_reparse_point_rejected",
                Assert.Throws<IOException>(() => PhysicalPathPolicy.NormalizeWithoutLinks(path)).Message);
        }
        finally
        {
            if ((File.GetAttributes(selected) & FileAttributes.ReparsePoint) != 0) Directory.Delete(selected);
        }
    }

    [Fact(DisplayName = "Nested Synchronous Attribute Scopes Release Their Own Handles In Owner Order")]
    public void RequiresOwnerOrder()
    {
        var instance = Create();
        var outer = DirectoryAttributeScope.Hold(instance);
        var inner = DirectoryAttributeScope.Hold(instance);
        try { Assert.Throws<InvalidOperationException>(outer.Dispose); }
        finally { inner.Dispose(); outer.Dispose(); }
        outer.Dispose();
        Directory.Move(instance.RuntimeRoot, instance.RuntimeRoot + "-released");
    }

    [Fact(DisplayName = "Declared Directories Are Pinned Without Remembering Earlier Missing Paths Or Child File Bytes")]
    public void ProtectsDeclaredPaths()
    {
        var instance = Create();
        var directory = Path.Combine(instance.RuntimeRoot, "declared");
        using (var protection = DirectoryAttributeProtection.Acquire(instance))
        using (DirectoryAttributeScope.Enter(protection))
        {
            protection.Include([directory]);
            Directory.CreateDirectory(directory);
            protection.Include([directory]);
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + "-moved"));
            var file = Path.Combine(directory, "input.txt");
            File.WriteAllText(file, "before");
            var scanner = new FileTreeScanner(SnapshotBudget.Default);
            var before = scanner.Capture(file, default);
            File.WriteAllText(file, "changed");
            Assert.NotEqual(before.Entries.Single().Sha256, scanner.Capture(file, default).Entries.Single().Sha256);
        }
        Directory.Move(directory, directory + "-moved");
    }

    [Fact(DisplayName = "Declared Directory Protection Retains At Most Sixty Four Handles And Releases Them Together")]
    public void BoundsDeclaredPaths()
    {
        var instance = Create();
        var directories = Enumerable.Range(0, 96).Select(index => Path.Combine(instance.RuntimeRoot, "declared-" + index)).ToArray();
        foreach (var directory in directories) Directory.CreateDirectory(directory);
        using (var protection = DirectoryAttributeProtection.Acquire(instance))
        using (DirectoryAttributeScope.Enter(protection))
        {
            protection.Include(directories);
            Assert.Throws<IOException>(() => Directory.Move(directories[0], directories[0] + "-moved"));
            Directory.Move(directories[^1], directories[^1] + "-moved");
        }
        Directory.Move(directories[0], directories[0] + "-moved");
    }

    [Theory(DisplayName = "An Empty Leaf Directory Cannot Move While Its Stable Or Identity Bound Lease Is Held")]
    [InlineData(false)]
    [InlineData(true)]
    public void PinsLeafDirectories(bool identityBound)
    {
        var instance = Create();
        var directory = Path.Combine(instance.RuntimeRoot, "empty-leaf");
        Directory.CreateDirectory(directory);
        using (IDisposable held = identityBound ? new PhysicalDirectoryLease(directory, PhysicalObjectIdentity.Read(directory)) :
            StableDirectoryLease.Acquire(directory))
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + "-moved"));
        Directory.Move(directory, directory + "-moved");
    }

    private static RuntimeInstance Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "directory-attributes-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new ContractReader()).OpenOrCreate(root);
        Directory.CreateDirectory(Path.Combine(root, "blobs", "plans"));
        return instance;
    }
}
