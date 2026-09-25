using System.Text;
using NexusPipeline.Modules.Configuration.Editing;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class ConfigRepairPolicyTests
{
    [Theory]
    [InlineData("Loop")]
    [InlineData("关机")]
    [InlineData("Restart")]
    public void KnownSystemActionProducesMinimalReviewablePatch(string action)
    {
        byte[] before = Encoding.UTF8.GetBytes($"\uFEFF# keep\r\nafter_finish: {action} # action\r\nsecret: 'leave-me'\r\n");
        ConfigRepairProposal? proposal = ConfigRepairPolicy.TryPropose(
            "march7th", "0.3.0", "user-a", "script-a", "profile-a", "locator-a", 3,
            before, out byte[]? after);

        Assert.NotNull(proposal);
        Assert.True(proposal.Available);
        Assert.Equal(action, proposal.OldValue);
        Assert.Equal("None", proposal.ProposedValue);
        Assert.Equal("\uFEFF# keep\r\nafter_finish: \"None\" # action\r\nsecret: 'leave-me'\r\n",
            Encoding.UTF8.GetString(after!));
        Assert.DoesNotContain("leave-me", System.Text.Json.JsonSerializer.Serialize(proposal));
        Assert.NotEqual(proposal.Token, ConfigRepairPolicy.Token(
            "march7th", "0.3.0", "user-b", "script-a", "profile-a", "locator-a", 3, before));
        Assert.NotEqual(proposal.Token, ConfigRepairPolicy.Token(
            "march7th", "0.3.0", "user-a", "script-a", "profile-a", "locator-a", 4, before));
    }

    [Theory]
    [InlineData("0.2.0", "Shutdown")]
    [InlineData("0.3.0", "MysteryAction")]
    [InlineData("0.3.0", "None")]
    public void UnsupportedVersionOrActionHasNoRepair(string version, string action)
    {
        ConfigRepairProposal? proposal = ConfigRepairPolicy.TryPropose(
            "march7th", version, "user", "script", "profile", "locator", 1,
            Encoding.UTF8.GetBytes($"after_finish: {action}\n"), out byte[]? after);
        Assert.Null(proposal);
        Assert.Null(after);
    }
}
