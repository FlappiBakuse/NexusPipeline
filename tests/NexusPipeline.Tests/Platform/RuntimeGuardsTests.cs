using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
namespace NexusPipeline.Tests.Platform;


public sealed class RuntimeGuardsTests
{
    [Fact]
    public void StableExitWindow_RequiresContinuousEmptyWindow()
    {
        var window = new StableExitWindow(TimeSpan.FromSeconds(3));
        DateTime start = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Local);

        Assert.False(window.Observe(hasOwnedProcess: false, start));
        Assert.False(window.Observe(hasOwnedProcess: true, start.AddSeconds(2)));
        Assert.False(window.Observe(hasOwnedProcess: false, start.AddSeconds(3)));
        Assert.True(window.Observe(hasOwnedProcess: false, start.AddSeconds(6)));
        Assert.True(window.IsStable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(3000)]
    [InlineData(5000)]
    public void StableExitWindow_RejectsRestartTimingsCoveredByHarness(int restartDelayMs)
    {
        var window = new StableExitWindow(TimeSpan.FromSeconds(SystemActions.StableExitSeconds));
        DateTime start = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc);

        Assert.False(window.Observe(hasOwnedProcess: false, start));
        Assert.False(window.Observe(hasOwnedProcess: true, start.AddMilliseconds(restartDelayMs)));
        Assert.False(window.Observe(
            hasOwnedProcess: false,
            start.AddMilliseconds(restartDelayMs)));
        Assert.True(window.Observe(
            hasOwnedProcess: false,
            start.AddMilliseconds(restartDelayMs + SystemActions.StableExitSeconds * 1000)));
    }

    [Fact]
    public void ProcessIdentity_DoesNotMatchReusedPid()
    {
        DateTime start = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Local);
        var identity = new ProcessIdentity(100, start, "script.exe");

        Assert.True(identity.Matches(new ProcessIdentity(100, start, "script.exe")));
        Assert.False(identity.Matches(new ProcessIdentity(100, start.AddTicks(1), "script.exe")));
        Assert.False(identity.Matches(new ProcessIdentity(101, start, "script.exe")));
        Assert.False(identity.Matches(new ProcessIdentity(100, start, "other.exe")));
    }

    [Theory]
    [InlineData("a1b2c3d4", true)]
    [InlineData("A1-b2_C3d4", true)]
    [InlineData("short", false)]
    [InlineData("contains space", false)]
    [InlineData("contains.dot", false)]
    public void RequesterWindowToken_UsesStableSafeShape(string token, bool expected)
    {
        Assert.Equal(expected, SystemActions.IsRequesterWindowTokenValid(token));
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.True(condition(), "条件在超时时间内未满足");
    }
}
