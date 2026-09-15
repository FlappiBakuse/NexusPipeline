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

    [Fact]
    public void UserListBadgeRoute_UsesSingleReadOnlyEndpoint()
    {
        Assert.True(ApiPluginContributionsHandler.TryParseUserListBadgesRoute(
            "GET",
            new[] { "plugin-contributions", "user-list-badges" }));
        Assert.False(ApiPluginContributionsHandler.TryParseUserListBadgesRoute(
            "PUT",
            new[] { "plugin-contributions", "user-list-badges" }));
        Assert.False(ApiPluginContributionsHandler.TryParseUserListBadgesRoute(
            "GET",
            new[] { "plugin-contributions", "user-list-badges", "extra" }));
    }

    [Fact]
    public void UiRoutesDecodePluginAndContributionIdentifiers()
    {
        Assert.True(ApiPluginContributionsHandler.TryParseUiSaveRoute(
            "PUT",
            new[] { "plugin-contributions", "ui", "better%2Fgi", "daily%2Dsummary" },
            out string pluginName,
            out string contributionId));
        Assert.Equal("better/gi", pluginName);
        Assert.Equal("daily-summary", contributionId);

        Assert.True(ApiPluginContributionsHandler.TryParseUiActionRoute(
            "POST",
            new[] { "plugin-contributions", "ui", "bettergi", "summary", "action", "refresh%2Dnow" },
            out pluginName,
            out contributionId,
            out string action));
        Assert.Equal("bettergi", pluginName);
        Assert.Equal("summary", contributionId);
        Assert.Equal("refresh-now", action);
    }

    [Fact]
    public void UiRoutesRejectWrongShapeAndWrongVerb()
    {
        Assert.False(ApiPluginContributionsHandler.TryParseUiQueryRoute(
            "GET",
            new[] { "plugin-contributions", "ui", "query" }));
        Assert.False(ApiPluginContributionsHandler.TryParseUiSaveRoute(
            "POST",
            new[] { "plugin-contributions", "ui", "bettergi", "summary" },
            out _,
            out _));
        Assert.False(ApiPluginContributionsHandler.TryParseUiActionRoute(
            "POST",
            new[] { "plugin-contributions", "ui", "bettergi", "summary", "action" },
            out _,
            out _,
            out _));
    }
}
