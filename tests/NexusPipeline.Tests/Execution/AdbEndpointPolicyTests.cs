using Xunit;
using NexusPipeline.Modules.Execution.Targets;
namespace NexusPipeline.Tests.Execution;


public sealed class AdbEndpointPolicyTests
{

    [Theory]
    [InlineData("127.0.0.1:16384", true)]
    [InlineData("localhost:16384", true)]
    [InlineData("[::1]:16384", true)]
    [InlineData("192.168.1.10:16384", false)]
    [InlineData("remote.example:16384", false)]
    public void AdbEndpointLoopbackPolicy_DistinguishesRemoteHosts(string address, bool expected)
    {
        Assert.Equal(expected, EmulatorSupport.IsLoopbackAdbEndpoint(address));
    }
}
