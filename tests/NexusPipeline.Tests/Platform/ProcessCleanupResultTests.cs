using Xunit;
using NexusPipeline.Platform.Processes;
namespace NexusPipeline.Tests.Platform;


public sealed class ProcessCleanupResultTests
{

    [Fact]
    public void CleanupResultCarriesRemainingProcessEvidence()
    {
        ProcessCleanupResult result = ProcessCleanupResult.Unconfirmed(
            new[] { 101, 202 },
            "Toolhelp 快照失败");

        Assert.False(result.ConfirmedExited);
        Assert.Equal(new[] { 101, 202 }, result.RemainingPids);
        Assert.Equal("Toolhelp 快照失败", result.Reason);
    }
}
