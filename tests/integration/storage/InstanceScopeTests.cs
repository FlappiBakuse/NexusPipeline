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
        using (var scope = InstanceReadScope.Hold(instance, contracts))
        {
            Assert.Equal(instance, new InstanceRoot(new ContractReader()).OpenExisting(root));
            Assert.Throws<IOException>(() => File.WriteAllText(path, "changed"));
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            using (InstanceReadScope.Hold(instance, contracts)) Assert.Equal(instance, new InstanceRoot(contracts).OpenExisting(root));
            Assert.Equal(instance, new InstanceRoot(contracts).OpenExisting(root));
        }
        File.WriteAllText(path, "changed");
        Assert.ThrowsAny<Exception>(() => new InstanceRoot(contracts).OpenExisting(root));
        Assert.Equal("changed", File.ReadAllText(path));
    }

    [Fact(DisplayName = "Nested Instance Read Owners Remain Isolated And Restore The Previous Identity")]
    public void IsolatesNestedOwners()
    {
        var contracts = new ContractReader();
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "instance-nested-" + Guid.NewGuid().ToString("N"));
        var first = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "first"));
        var second = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "second"));
        using var outer = InstanceReadScope.Hold(first, contracts);
        using var inner = InstanceReadScope.Hold(second, contracts);
        Assert.Equal(first, new InstanceRoot(contracts).OpenExisting(first.RuntimeRoot));
        Assert.Equal(second, new InstanceRoot(contracts).OpenExisting(second.RuntimeRoot));
        Assert.Throws<InvalidOperationException>(() => outer.Dispose());
        inner.Dispose();
        Assert.Equal(first, new InstanceRoot(contracts).OpenExisting(first.RuntimeRoot));
        Assert.Throws<IOException>(() => InstanceReadScope.Hold(first with { InstanceId = Guid.NewGuid() }, contracts));
    }
}
