using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.Tests.Platform;

public sealed class FirewallRuleTests
{
    [Fact]
    public void TestHostDryRunDoesNotInvokeTheWindowsCommand()
    {
        string? previous = Environment.GetEnvironmentVariable("NEXUS_SYSTEM_ACTION_DRYRUN");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_SYSTEM_ACTION_DRYRUN", "1");
            int calls = 0;
            FirewallRule.EnsureAllowInbound(58731, _ => { calls++; return 0; });
            Assert.Equal(0, calls);
        }
        finally { Environment.SetEnvironmentVariable("NEXUS_SYSTEM_ACTION_DRYRUN", previous); }
    }

    [Fact]
    public void RuleUpdateUsesOneAddOnlyAfterSetFailsAndRejectsInvalidPorts()
    {
        string? previous = Environment.GetEnvironmentVariable("NEXUS_SYSTEM_ACTION_DRYRUN");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_SYSTEM_ACTION_DRYRUN", null);
            var calls = new List<string>();
            FirewallRule.EnsureAllowInbound(58731, args => { calls.Add(args); return calls.Count == 1 ? 1 : 0; });
            Assert.Equal(new[] { FirewallRule.BuildSetRuleArguments(58731), FirewallRule.BuildAddRuleArguments(58731) }, calls);
            calls.Clear();
            FirewallRule.EnsureAllowInbound(58731, args => { calls.Add(args); return 0; });
            Assert.Equal(new[] { FirewallRule.BuildSetRuleArguments(58731) }, calls);
            FirewallRule.EnsureAllowInbound(0, args => { calls.Add(args); return 0; });
            FirewallRule.EnsureAllowInbound(65536, args => { calls.Add(args); return 0; });
            Assert.Single(calls);
        }
        finally { Environment.SetEnvironmentVariable("NEXUS_SYSTEM_ACTION_DRYRUN", previous); }
    }
}
