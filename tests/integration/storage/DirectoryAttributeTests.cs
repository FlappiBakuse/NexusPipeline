using System.ComponentModel;
using System.Security.Cryptography;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DirectoryAttributeTests
{
    [Fact(DisplayName = "Retained Directory Attributes Never Cache Or Lock The Contents Of Their Child Files")]
    public void ReadsCurrentFiles()
    {
        var instance = Create();
        var path = Path.Combine(instance.RuntimeRoot, "blobs", "plans", "input.txt");
        byte[] original = [1, 2, 3];
        File.WriteAllBytes(path, original);
        using var scope = DirectoryAttributeScope.Hold(instance);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var before = scanner.Capture(path, default);
        byte[] updated = [4, 5, 6];
        File.WriteAllBytes(path, updated);
        var after = scanner.Capture(path, default);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), Assert.Single(before.Entries).Sha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(updated)), Assert.Single(after.Entries).Sha256);
    }

    [Theory(DisplayName = "A Retained Parent Still Rejects Newly Introduced Live Or Broken Descendant Junctions")]
    [InlineData(false)]
    [InlineData(true)]
    public void RechecksDescendantLinks(bool broken)
    {
        var instance = Create();
        var parent = Path.Combine(instance.RuntimeRoot, "journals");
        var linked = Path.Combine(parent, "introduced");
        var outside = instance.RuntimeRoot + "-outside";
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
        Assert.Equal(path, PhysicalPathPolicy.NormalizeWithoutLinks(path));
    }

    [Fact(DisplayName = "A Retained Directory Uses Fresh Native Reparse Attributes Instead Of Its Initial Attribute Value")]
    public void RechecksRetainedTags()
    {
        var instance = Create();
        var selected = Path.Combine(instance.RuntimeRoot, "blobs", "plans");
        var outside = instance.RuntimeRoot + "-outside";
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
        var nested = Create();
        var moved = instance.RuntimeRoot + "-released";
        var nestedMoved = nested.RuntimeRoot + "-released";
        using (var outer = DirectoryAttributeScope.Hold(instance))
        {
            using (DirectoryAttributeScope.Hold(nested))
            {
                Assert.Equal("directory_attributes_scope_order", Assert.Throws<InvalidOperationException>(outer.Dispose).Message);
                Assert.Throws<IOException>(() => Directory.Move(nested.RuntimeRoot, nestedMoved));
            }
            Directory.Move(nested.RuntimeRoot, nestedMoved);
            Assert.Throws<IOException>(() => Directory.Move(instance.RuntimeRoot, moved));
        }
        Directory.Move(instance.RuntimeRoot, moved);
    }

    [Fact(DisplayName = "A Declared Directory Is Pinned After It Appears And Released With Its Protection")]
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
        }
        Directory.Move(directory, directory + "-moved");
    }

    [Fact(DisplayName = "Declared Directory Protection Retains At Most Sixty Four Handles And Releases Them Together")]
    public void BoundsDeclaredPaths()
    {
        var instance = Create();
        var directories = Enumerable.Range(0, 96).Select(index => Path.Combine(instance.RuntimeRoot, "declared-" + index)).ToArray();
        foreach (var directory in directories) Directory.CreateDirectory(directory);
        var pinned = new List<string>();
        using (var protection = DirectoryAttributeProtection.Acquire(instance))
        using (DirectoryAttributeScope.Enter(protection))
        {
            protection.Include(directories);
            foreach (var directory in directories)
            {
                try { Directory.Move(directory, directory + "-moved"); }
                catch (IOException) { pinned.Add(directory); }
            }
            Assert.InRange(pinned.Count, 1, 64);
        }
        foreach (var directory in pinned) Directory.Move(directory, directory + "-moved");
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
