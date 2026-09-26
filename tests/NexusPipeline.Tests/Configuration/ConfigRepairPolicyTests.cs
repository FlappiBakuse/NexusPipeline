using System.Text;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Plugins.Contracts;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Settings;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class ConfigRepairPolicyTests
{
    [Fact]
    public void BackendRepairEndpointsAreDisabledByDefault()
    {
        var commands = new ConfigEditCommands(null!, null!, null!, null!, null!, null!, null!,
            settingsProvider: new SettingsState(new AppSettings()));
        Assert.Equal("config_repair_disabled", commands.PreviewRepair("script", "user").Error?.Code);
        Assert.Equal("config_repair_disabled", commands.ApplyRepair("script", "user", new string('a', 64)).Error?.Code);
    }

    private static TaskConfigRepairDescriptor Rule => new("queue_finish_action", "march7th.finish_action",
        "config:config.yaml", new JsonArray("after_finish"), "user_snapshot", "yaml",
        ["Loop", "循环", "Shutdown", "关机", "Sleep", "睡眠", "Hibernate", "休眠",
         "Restart", "重启", "Logoff", "注销", "TurnOffDisplay", "关闭显示器"],
        "None", "脚本结束后不再执行此系统动作；其他配置字段保持原值。");

    [Theory]
    [InlineData("Loop")]
    [InlineData("关机")]
    [InlineData("Restart")]
    public void KnownSystemActionProducesMinimalReviewablePatch(string action)
    {
        byte[] before = Encoding.UTF8.GetBytes($"\uFEFF# keep\r\nafter_finish: {action} # action\r\nsecret: 'leave-me'\r\n");
        ConfigRepairProposal? proposal = ConfigRepairPolicy.TryPropose(
            Rule, "march7th", "0.3.1", "user-a", "script-a", "profile-a", "locator-a", 3,
            before, out byte[]? after);

        Assert.NotNull(proposal);
        Assert.True(proposal.Available);
        Assert.Equal(action, proposal.OldValue);
        Assert.Equal("None", proposal.ProposedValue);
        Assert.Equal("\uFEFF# keep\r\nafter_finish: \"None\" # action\r\nsecret: 'leave-me'\r\n",
            Encoding.UTF8.GetString(after!));
        Assert.DoesNotContain("leave-me", System.Text.Json.JsonSerializer.Serialize(proposal));
        Assert.NotEqual(proposal.Token, ConfigRepairPolicy.Token(
            "march7th", "0.3.1", "user-b", "script-a", "profile-a", "locator-a", 3, before, Rule));
        Assert.NotEqual(proposal.Token, ConfigRepairPolicy.Token(
            "march7th", "0.3.1", "user-a", "script-a", "profile-a", "locator-a", 4, before, Rule));
        Assert.NotEqual(proposal.Token, ConfigRepairPolicy.Token(
            "march7th", "0.3.1", "user-a", "script-a", "profile-a", "locator-a", 3, before,
            Rule with { Explanation = "changed" }));
    }

    [Theory]
    [InlineData("MysteryAction")]
    [InlineData("None")]
    public void UnsupportedActionHasNoRepair(string action)
    {
        ConfigRepairProposal? proposal = ConfigRepairPolicy.TryPropose(
            Rule, "march7th", "0.3.1", "user", "script", "profile", "locator", 1,
            Encoding.UTF8.GetBytes($"after_finish: {action}\n"), out byte[]? after);
        Assert.Null(proposal);
        Assert.Null(after);
    }

    [Fact]
    public void UndeclaredSourceValueHasNoRepair()
    {
        ConfigRepairProposal? proposal = ConfigRepairPolicy.TryPropose(
            Rule with { FromValues = ["Loop"] }, "march7th", "0.3.1", "user", "script",
            "profile", "locator", 1, Encoding.UTF8.GetBytes("after_finish: Shutdown\n"), out byte[]? after);
        Assert.Null(proposal);
        Assert.Null(after);
    }
}
