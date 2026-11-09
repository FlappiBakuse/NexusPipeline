using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DirectoryProtectionTests
{
    [Theory(DisplayName = "Deep Ancestors Beyond The Retained Handle Bound Still Reject Reparse Changes Before And After Acquisition")]
    [InlineData(false)]
    [InlineData(true)]
    public void RechecksDeepAncestors(bool afterAcquisition)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var deep = Enumerable.Range(0, 70).Aggregate(root, (path, _) => Path.Combine(path, "d"));
        var instance = new InstanceRoot(new ContractReader()).OpenOrCreate(deep);
        var selected = Path.Combine(deep, "blobs", "plans");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(selected); Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "marker.txt"); File.WriteAllText(marker, "unchanged");
        using var protection = afterAcquisition ? DirectoryAttributeProtection.Acquire(instance) : null;
        Directory.Delete(selected);
        ControlledJunction.Create(selected, outside);
        try
        {
            IOException rejection;
            if (protection is null)
                rejection = Assert.Throws<IOException>(() => DirectoryAttributeProtection.Acquire(instance));
            else
            {
                using var scope = DirectoryAttributeScope.Enter(protection);
                rejection = Assert.Throws<IOException>(() => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(selected, "marker.txt")));
            }
            Assert.Equal("filesystem_reparse_point_rejected", rejection.Message);
            Assert.Equal("unchanged", File.ReadAllText(marker));
        }
        finally { Directory.Delete(selected); }
    }

    [Fact(DisplayName = "Explicit Physical Claim Directories Stay Pinned Only During Their Synchronous Read Scope")]
    public void HoldsClaimDirectories()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var selected = Path.Combine(root, "inputs"); var moved = selected + "-moved";
        Directory.CreateDirectory(selected);
        var file = Path.Combine(selected, "selected.bin"); File.WriteAllText(file, "original");
        var identity = PhysicalObjectIdentity.Read(file);
        using (DirectoryAttributeScope.HoldDirectories([selected, selected]))
        {
            Assert.Equal(file, PhysicalPathPolicy.NormalizeWithoutLinks(file));
            Assert.Throws<IOException>(() => Directory.Move(selected, moved));
            File.WriteAllText(file, "changed");
        }
        Directory.Move(selected, moved);
        ControlledJunction.Create(selected, moved);
        try
        {
            Assert.Equal("filesystem_reparse_point_rejected",
                Assert.Throws<IOException>(() => DirectoryAttributeScope.HoldDirectories([selected])).Message);
        }
        finally { Directory.Delete(selected); }
        var movedFile = Path.Combine(moved, "selected.bin");
        Assert.Equal(movedFile, PhysicalPathPolicy.NormalizeWithoutLinks(movedFile));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(movedFile));
        Assert.Equal("changed", File.ReadAllText(movedFile));
    }

    [Fact(DisplayName = "An Owned Directory Stays Pinned Across Synchronous Checks And Is Movable After Release")]
    public async Task SharesOwnedProtection()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new ContractReader()).OpenOrCreate(root);
        var path = Path.Combine(root, "blobs", "plans");
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, "marker.json");
        File.WriteAllText(file, "preserve");
        var identity = PhysicalObjectIdentity.Read(file);
        var moved = root + "-moved";
        var protection = DirectoryAttributeProtection.Acquire(instance);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                using var scope = DirectoryAttributeScope.Enter(protection);
                Assert.Equal(identity, PhysicalObjectIdentity.Read(file));
                Assert.Throws<IOException>(() => Directory.Move(root, moved));
            })));
            Assert.Throws<IOException>(() => Directory.Move(root, moved));
        }
        finally { protection.Dispose(); }
        using (DirectoryAttributeScope.Enter(protection))
            Assert.Throws<ObjectDisposedException>(() => PhysicalPathPolicy.NormalizeWithoutLinks(path));
        Directory.Move(root, moved);
        var movedFile = Path.Combine(moved, "blobs", "plans", "marker.json");
        Assert.Equal(identity, PhysicalObjectIdentity.Read(movedFile));
        Assert.Equal("preserve", File.ReadAllText(movedFile));
    }
}
