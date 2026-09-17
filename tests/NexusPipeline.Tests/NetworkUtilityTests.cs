using System.Net;
using MailKit.Security;
using MimeKit;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Notification;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class NetworkUtilityTests
{
    [Fact]
    public void FirewallRule_BuildsSetAndAddArguments()
    {
        Assert.Equal(
            "advfirewall firewall set rule name=\"NexusPipeline Web TCP\" new dir=in action=allow protocol=TCP localport=58731 enable=yes profile=private,public",
            FirewallRule.BuildSetRuleArguments(58731));
        Assert.Equal(
            "advfirewall firewall add rule name=\"NexusPipeline Web TCP\" dir=in action=allow protocol=TCP localport=58731 enable=yes profile=private,public",
            FirewallRule.BuildAddRuleArguments(58731));
    }

    [Fact]
    public void NetInfo_NormalizesOnlyDistinctLanIpv4Addresses()
    {
        List<string> addresses = NetInfo.NormalizeLanAddresses(new[]
        {
            " 192.168.1.20 ",
            "192.168.1.20",
            "10.0.0.2",
            "127.0.0.1",
            "::1",
            "not-an-address",
        });

        Assert.Equal(new[] { "10.0.0.2", "192.168.1.20" }, addresses);
    }
}
