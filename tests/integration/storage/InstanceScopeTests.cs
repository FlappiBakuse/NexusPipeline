using System.Text.Json;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class InstanceScopeTests
{
    [Fact(DisplayName = "Protected Instance Reads Keep Exact Identity Bytes And Names Until Release And Reject Cold Changes")]
    public void ProtectsIdentityReads()
    {
        var contracts = new ContractReader();
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "instance-read-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(contracts).OpenOrCreate(root);
        var path = Path.Combine(root, "instance.json");
        var bytes = File.ReadAllBytes(path);
        var identity = PhysicalObjectIdentity.Read(path);
        using (InstanceReadScope.Hold(instance, contracts))
        {
            using (InstanceReadScope.Hold(instance, contracts))
            {
                Assert.Equal(instance, new InstanceRoot(contracts).OpenExisting(root));
                Assert.Throws<IOException>(() => File.WriteAllText(path, "changed"));
                Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
            }
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        }
        File.WriteAllText(path, "changed");
        Assert.Throws<JsonException>(() => new InstanceRoot(contracts).OpenExisting(root));
        Assert.Equal("changed", File.ReadAllText(path));
    }

    [Fact(DisplayName = "Nested Instance Read Owners Remain Isolated And Restore The Previous Identity")]
    public void IsolatesNestedOwners()
    {
        var contracts = new ContractReader();
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "instance-nested-" + Guid.NewGuid().ToString("N"));
        var instances = new InstanceRoot(contracts);
        var first = instances.OpenOrCreate(Path.Combine(root, "first"));
        var second = instances.OpenOrCreate(Path.Combine(root, "second"));
        using var outer = InstanceReadScope.Hold(first, contracts);
        var moved = second.RuntimeRoot + "-released";
        using (InstanceReadScope.Hold(second, contracts))
        {
            Assert.Equal(second, instances.OpenExisting(second.RuntimeRoot));
            Assert.Throws<InvalidOperationException>(() => outer.Dispose());
            Assert.Throws<IOException>(() => Directory.Move(second.RuntimeRoot, moved));
        }
        Directory.Move(second.RuntimeRoot, moved);
        Assert.Throws<IOException>(() => instances.OpenExisting(moved));
        Assert.Throws<IOException>(() => Directory.Move(first.RuntimeRoot, first.RuntimeRoot + "-moved"));
        Assert.Throws<IOException>(() => InstanceReadScope.Hold(first with { InstanceId = Guid.NewGuid() }, contracts));
        Assert.Equal(first, instances.OpenExisting(first.RuntimeRoot));
    }
}
