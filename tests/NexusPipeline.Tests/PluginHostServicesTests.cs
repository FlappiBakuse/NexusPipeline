using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginHostServicesTests
{
    [Fact]
    public void ContributionValidation_SanitizesSecretsAndRejectsUnknownFields()
    {
        var contribution = new PluginUserGlobalManagementContribution(
            "settings",
            "设置",
            "测试",
            1,
            new[]
            {
                new PluginUserGlobalManagementField("name", "名称", "text", "", MaxLength: 20),
                new PluginUserGlobalManagementField("token", "令牌", "secret", "", MaxLength: 20),
            },
            (_, _) => ValueTask.FromResult(new JsonObject
            {
                ["name"] = "用户",
                ["token"] = new JsonObject { ["value"] = "secret-value" },
            }),
            (_, _, _) => ValueTask.CompletedTask);
        var registry = new PluginUserGlobalManagementRegistry();
        using IDisposable registration = registry.Register("fixture", "Fixture", contribution);
        PluginUserGlobalManagementRegistration saved = Assert.Single(registry.Snapshot());

        JsonObject sanitized = PluginContributionValidation.SanitizeRead(
            saved,
            new JsonObject
            {
                ["name"] = "用户",
                ["token"] = new JsonObject { ["value"] = "secret-value" },
            });

        Assert.Equal("用户", sanitized["name"]?.ToString());
        Assert.True(sanitized["token"]?["configured"]?.GetValue<bool>());
        Assert.Null(sanitized["token"]?["value"]);
        Assert.True(PluginContributionValidation.TryValidateSave(
            saved,
            new JsonObject
            {
                ["NAME"] = "新用户",
                ["TOKEN"] = new JsonObject { ["action"] = "keep" },
            },
            out JsonObject canonical,
            out string canonicalError), canonicalError);
        Assert.Equal("新用户", canonical["name"]?.ToString());
        Assert.Equal("keep", canonical["token"]?["action"]?.ToString());
        Assert.False(PluginContributionValidation.TryValidateSave(
            saved,
            new JsonObject { ["unknown"] = "value" },
            out _,
            out string error));
        Assert.Contains("未知", error);
    }

    [Fact]
    public async Task ExecutionEvents_ArePublishedAsynchronously()
    {
        var completion = new TaskCompletionSource<PluginUserRunStartingEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new PluginExecutionEventRegistry((_, _) => { });
        using IDisposable subscription = events.Subscribe("fixture", eventData =>
        {
            completion.TrySetResult(eventData);
            return ValueTask.CompletedTask;
        });

        var expected = new PluginUserRunStartingEvent("u1", "用户", "s1", "脚本", "q1", "队列", "auto", DateTimeOffset.Now);
        events.Publish(expected);

        PluginUserRunStartingEvent actual = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task UserListBadges_AreSortedSanitizedAndRemovedWithRegistration()
    {
        var registry = new PluginUserListBadgeRegistry();
        using IDisposable later = registry.Register(
            "later-plugin",
            "Later",
            new PluginUserListBadgeContribution(
                "later-badge",
                200,
                (_, _) => ValueTask.FromResult<PluginUserListBadge?>(new PluginUserListBadge("稍后", "OK", "提示"))));
        using IDisposable first = registry.Register(
            "first-plugin",
            "First",
            new PluginUserListBadgeContribution(
                "first-badge",
                100,
                (_, _) => ValueTask.FromResult<PluginUserListBadge?>(new PluginUserListBadge("首先"))));

        Assert.Equal(new[] { "first-badge", "later-badge" }, registry.Snapshot().Select(item => item.Contribution.Id));
        PluginUserListBadge? badge = await registry.Snapshot()[1].Contribution.ReadHandler("user-1", CancellationToken.None);
        Assert.True(PluginContributionValidation.TrySanitizeUserListBadge(badge, out PluginUserListBadge? sanitized, out string error), error);
        Assert.Equal("ok", sanitized!.Tone);
        Assert.Equal("提示", sanitized.Title);
        Assert.False(PluginContributionValidation.TrySanitizeUserListBadge(
            new PluginUserListBadge("非法", "custom-css"),
            out _,
            out string invalidError));
        Assert.Contains("tone", invalidError);

        first.Dispose();
        Assert.Equal("later-badge", Assert.Single(registry.Snapshot()).Contribution.Id);
        registry.Clear();
        Assert.Empty(registry.Snapshot());
    }
}
