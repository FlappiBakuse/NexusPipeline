using NexusPipeline.Platform.Networking;
using Xunit;

namespace NexusPipeline.Tests.Platform;

public sealed class RemoteAccessAddressTests
{
    [Fact]
    public void PreferredGatewayProducesOneInternalAndOnePublicAddress()
    {
        NetworkAddressCandidate[] candidates =
        [
            new("172.21.16.1", false, true), new("172.21.176.1", false, true),
            new("198.18.0.1", true, false), new("192.168.1.8", false, false),
            new("192.168.124.12", true, false), new("8.8.8.8", true, false),
            new("1.1.1.1", false, false), new("2001:db8::1", true, false),
        ];
        Assert.Equal(new RemoteAccessAddresses("192.168.124.12", "8.8.8.8"), NetInfo.SelectRemoteAccessAddresses(candidates));
        Assert.Equal(NetInfo.SelectRemoteAccessAddresses(candidates), NetInfo.SelectRemoteAccessAddresses(candidates.Reverse()));
    }

    [Fact]
    public void NatAndReservedAddressesCannotInventAPublicEndpoint()
    {
        string[] reserved = ["0.0.0.0", "127.0.0.1", "100.64.0.1", "169.254.1.2", "192.0.0.8", "192.0.2.1", "192.88.99.1", "198.18.0.1", "198.19.1.1", "198.51.100.1", "203.0.113.1", "224.0.0.1", "255.255.255.255", "invalid"];
        var candidates = reserved.Select(address => new NetworkAddressCandidate(address, true, false))
            .Append(new("192.168.124.12", true, false))
            .Append(new("1.1.1.1", true, true));
        Assert.Equal(new RemoteAccessAddresses("192.168.124.12", null), NetInfo.SelectRemoteAccessAddresses(candidates));
        Assert.Equal(new RemoteAccessAddresses(null, null), NetInfo.SelectRemoteAccessAddresses([]));
    }
}
