using Xunit;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Tests.Support;

namespace NexusPipeline.Tests.Plugins;

public sealed class PluginManagerTests : IClassFixture<HostTestScope>
{
    private readonly HostCompositionRoot _context;

    public PluginManagerTests(HostTestScope host)
    {
        _context = host.Composition;
    }

    [Fact]
    public void LoadAll_DoesNotExposeRemovedBuiltInPlugins()
    {
        PluginManager manager = _context.Plugins;
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
        PluginManager manager = _context.Plugins;
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
