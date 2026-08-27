using System.Text.Json.Nodes;
using NexusPipeline.App.Contracts;
using NexusPipeline.Mcp;
using NexusPipeline.Models;
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

    [Fact]
    public void SafeSettingsPatch_has_no_remote_access_or_secret_fields()
    {
        var patch = new McpSafeSettingsPatch
        {
            LightweightMode = true,
            WebPort = 58801,
        };

        JsonObject json = patch.ToPatch();

        Assert.True(json["lightweightMode"]!.GetValue<bool>());
        Assert.Equal(58801, json["webPort"]!.GetValue<int>());
        Assert.Null(json["allowRemoteAccess"]);
        Assert.Null(json["accessToken"]);
        Assert.Null(json["secretKey"]);
        Assert.Null(json["mcpAllowDestructiveTools"]);
    }

    [Fact]
    public void SecretKeyPolicy_allows_only_known_secret_slots()
    {
        Assert.True(McpPolicy.IsSecretKey("accessToken"));
        Assert.True(McpPolicy.IsSecretKey("smtpPassword"));
        Assert.False(McpPolicy.IsSecretKey("allowRemoteAccess"));
        Assert.False(McpPolicy.IsSecretKey("arbitraryPath"));
    }
}
