using NexusPipeline;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class RestartCommandTests
{
    [Fact]
    public void SafeRestartCommandPreservesServiceOrWebOnlyModeAndHandoff()
    {
        const string handoff = "handoff-token";

        string[] serviceArguments = Bootstrap.BuildRestartArguments(handoff, webOnly: false);
        string[] webArguments = Bootstrap.BuildRestartArguments(handoff, webOnly: true);

        Assert.Equal(new[] { "restart", "--handoff", handoff }, serviceArguments);
        Assert.Equal(new[] { "restart", "--web", ApplicationHost.KeepWebOnlyAliveArgument, "--handoff", handoff }, webArguments);
        Assert.False(ApplicationHost.ReadRestartWebOnly(serviceArguments));
        Assert.True(ApplicationHost.ReadRestartWebOnly(webArguments));
        Assert.False(ApplicationHost.ReadRestartKeepWebOnlyAlive(serviceArguments));
        Assert.True(ApplicationHost.ReadRestartKeepWebOnlyAlive(webArguments));
        Assert.Equal(handoff, ApplicationHost.ReadRestartHandoff(webArguments));
    }
}
