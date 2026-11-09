using System.Net;
using NexusPipeline.Platform.Networking;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class NetworkAddressTests
{
    [Theory]
    [InlineData("198.18.0.0", false)]
    [InlineData("198.19.255.255", false)]
    [InlineData("198.17.255.255", true)]
    [InlineData("198.20.0.0", true)]
    [InlineData("0.1.2.3", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("100.64.0.0", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("192.0.0.8", false)]
    [InlineData("192.0.0.9", true)]
    [InlineData("192.0.0.10", true)]
    [InlineData("192.0.2.1", false)]
    [InlineData("192.88.99.1", false)]
    [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("::1", false)]
    public void ClassifiesPublicAddresses(string address, bool expected)
    {
        var projected = NetworkAccessAddresses.Project([IPAddress.Parse(address)], 5000);
        Assert.Equal(expected ? new[] { $"http://{address}:5000" } : Array.Empty<string>(), projected.Public);
    }
}
