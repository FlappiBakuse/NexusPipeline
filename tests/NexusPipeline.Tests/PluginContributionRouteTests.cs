using NexusPipeline.Web;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginContributionRouteTests
{
    [Fact]
    public void GetRoute_UsesFullApiSegments()
    {
        bool parsed = ApiPluginContributionsHandler.TryParseRoute(
            "GET",
            new[] { "plugin-contributions", "user-global", "user%2F1" },
            out string userId,
            out string pluginName,
            out string contributionId);

        Assert.True(parsed);
        Assert.Equal("user/1", userId);
        Assert.Empty(pluginName);
        Assert.Empty(contributionId);
    }

    [Fact]
    public void PutRoute_UsesFullApiSegments()
    {
        bool parsed = ApiPluginContributionsHandler.TryParseRoute(
            "PUT",
            new[] { "plugin-contributions", "user-global", "user-1", "hoyolab", "check-in" },
            out string userId,
            out string pluginName,
            out string contributionId);

        Assert.True(parsed);
        Assert.Equal("user-1", userId);
        Assert.Equal("hoyolab", pluginName);
        Assert.Equal("check-in", contributionId);
    }

    [Fact]
    public void RouteRejectsMissingResourceSegmentOrUnsupportedMethod()
    {
        Assert.False(ApiPluginContributionsHandler.TryParseRoute(
            "GET",
            new[] { "user-global", "user-1" },
            out _,
            out _,
            out _));
        Assert.False(ApiPluginContributionsHandler.TryParseRoute(
            "POST",
            new[] { "plugin-contributions", "user-global", "user-1" },
            out _,
            out _,
            out _));
    }
}
