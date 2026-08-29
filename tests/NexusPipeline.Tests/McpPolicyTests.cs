using System.Text.Json.Nodes;
using NexusPipeline.App.Contracts;
using NexusPipeline.Mcp;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class McpPolicyTests
{
    [Theory]
    [InlineData("sleep")]
    [InlineData("reboot")]
    [InlineData("shutdown")]
    [InlineData("exit")]
    public void QueuePolicy_rejects_system_completion_actions(string action)
    {
        OperationResult<DispatchQueue> result = McpPolicy.ValidateQueue(new DispatchQueue
        {
            Name = "agent-queue",
            CompletionAction = action,
        });

        Assert.False(result.Succeeded);
        Assert.Equal("dangerous_completion_action", result.ErrorCode);
        Assert.Equal(OperationErrorKind.Forbidden, result.ErrorKind);
    }

    [Theory]
    [InlineData("sleep")]
    [InlineData("reboot")]
    [InlineData("shutdown")]
    [InlineData("exit")]
    public void QueueExecutionPolicy_rejects_existing_system_completion_actions(string action)
    {
        OperationResult<DispatchQueue> result = McpPolicy.ValidateQueueExecution(new DispatchQueue
        {
            Name = "existing-queue",
            CompletionAction = action,
        });

        Assert.False(result.Succeeded);
        Assert.Equal("dangerous_completion_action", result.ErrorCode);
        Assert.Equal(OperationErrorKind.Forbidden, result.ErrorKind);
    }

    [Fact]
    public void QueueExecutionPolicy_accepts_existing_queue_without_completion_action()
    {
        OperationResult<DispatchQueue> result = McpPolicy.ValidateQueueExecution(new DispatchQueue
        {
            Name = "safe-existing-queue",
            CompletionAction = "none",
        });

        Assert.True(result.Succeeded);
        Assert.Equal("safe-existing-queue", result.Value?.Name);
    }

    [Fact]
    public void SafeSettingsPatch_has_no_remote_access_or_secret_fields()
    {
        var patch = new McpSafeSettingsPatch
        {
            LightweightMode = true,
            WebPort = 58801,
            ProxyMode = "system",
            ProxyUrl = "https://proxy.example",
            ProxyUsername = "proxy-user",
        };

        JsonObject json = patch.ToPatch();

        Assert.True(json["lightweightMode"]!.GetValue<bool>());
        Assert.Equal(58801, json["webPort"]!.GetValue<int>());
        Assert.Null(json["allowRemoteAccess"]);
        Assert.Null(json["accessToken"]);
        Assert.Null(json["secretKey"]);
        Assert.Null(json["mcpAllowDestructiveTools"]);
        Assert.Equal("system", json["proxyMode"]!.GetValue<string>());
        Assert.Equal("https://proxy.example", json["proxyUrl"]!.GetValue<string>());
        Assert.Equal("proxy-user", json["proxyUsername"]!.GetValue<string>());
        Assert.Null(json["proxyPassword"]);
    }

    [Fact]
    public void SecretKeyPolicy_allows_only_known_secret_slots()
    {
        Assert.True(McpPolicy.IsSecretKey("accessToken"));
        Assert.True(McpPolicy.IsSecretKey("smtpPassword"));
        Assert.True(McpPolicy.IsSecretKey("proxyPassword"));
        Assert.False(McpPolicy.IsSecretKey("allowRemoteAccess"));
        Assert.False(McpPolicy.IsSecretKey("arbitraryPath"));
    }

    [Fact]
    public void PluginSettingPolicy_requires_destructive_permission_for_secret_set_or_clear()
    {
        var registration = new PluginUserGlobalManagementRegistration(
            Guid.NewGuid(),
            "fixture",
            "Fixture",
            new PluginUserGlobalManagementContribution(
                "settings",
                "设置",
                "测试",
                1,
                new[]
                {
                    new PluginUserGlobalManagementField("token", "令牌", "secret"),
                    new PluginUserGlobalManagementField("enabled", "启用", "switch"),
                },
                (_, _) => ValueTask.FromResult(new JsonObject()),
                (_, _, _) => ValueTask.CompletedTask));

        Assert.True(McpPolicy.HasSensitivePluginSettingChange(
            registration,
            new JsonObject { ["token"] = new JsonObject { ["action"] = "set", ["value"] = "secret" } }));
        Assert.True(McpPolicy.HasSensitivePluginSettingChange(
            registration,
            new JsonObject { ["token"] = new JsonObject { ["action"] = "clear" } }));
        Assert.False(McpPolicy.HasSensitivePluginSettingChange(
            registration,
            new JsonObject { ["token"] = new JsonObject { ["action"] = "keep" } }));
        Assert.False(McpPolicy.HasSensitivePluginSettingChange(
            registration,
            new JsonObject { ["enabled"] = true }));
    }
}
