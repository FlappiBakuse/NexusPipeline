using NexusPipeline.Plugins;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginManagerTests
{
    [Fact]
    public void LoadAll_DoesNotExposeRemovedBuiltInPlugins()
    {
        PluginManager manager = RuntimeContext.Instance.Plugins;
        manager.LoadAll();
        string[] firstNames = manager.PluginSummaries.Select(plugin => plugin.Name).OrderBy(name => name).ToArray();

        manager.LoadAll();
        string[] secondNames = manager.PluginSummaries.Select(plugin => plugin.Name).OrderBy(name => name).ToArray();

        Assert.Equal(firstNames, secondNames);
        Assert.DoesNotContain("notify", firstNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("emulator-adapter", firstNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagementProjection_IsCachedUntilInvalidated()
    {
        PluginManager manager = RuntimeContext.Instance.Plugins;
        manager.LoadAll();

        IReadOnlyList<PluginManagementView> first = manager.PluginManagementViews;
        IReadOnlyList<PluginManagementView> second = manager.PluginManagementViews;

        Assert.Same(first, second);
        long revision = manager.PluginManagementRevision;
        manager.InvalidateManagementSnapshot();

        Assert.True(manager.PluginManagementRevision > revision);
        IReadOnlyList<PluginManagementView> refreshed = manager.PluginManagementViews;
        Assert.Equal(first, refreshed);
        if (first.Count > 0)
        {
            Assert.NotSame(first, refreshed);
        }
    }
}
