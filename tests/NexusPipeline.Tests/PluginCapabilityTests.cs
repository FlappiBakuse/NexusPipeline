using NexusPipeline.Extensibility;
using NexusPipeline.Plugins;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginCapabilityTests
{
    [Fact]
    public void PluginCapabilityRegistry_UsesGenericCapabilityLookupAndReloadIsIdempotent()
    {
        var registry = new PluginCapabilityRegistry();
        var capability = new TestCapability();
        registry.Register("demo", capability);
        registry.RegisterKeys("demo", new[] { "probe", "emulator" });

        Assert.Single(registry.GetAll<TestCapability>(_ => true));
        Assert.True(registry.HasKey("demo", "probe", _ => true));
        Assert.Empty(registry.GetAll<TestCapability>(_ => false));
        Assert.False(registry.HasKey("demo", "probe", _ => false));

        registry.Clear();
        registry.Register("demo", capability);
        Assert.Single(registry.GetAll<TestCapability>(_ => true));
    }

    private sealed class TestCapability : IPluginCapability
    {
    }
}
