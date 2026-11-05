using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class AutomaticUpdateTests
{
    [Theory(DisplayName = "Automatic Extension Refresh Respects Content Channel And Version")]
    [InlineData("0.5.0", "0.5.0", false, true, false, true)]
    [InlineData("0.5.0", "0.5.0", false, false, true, true)]
    [InlineData("0.5.0", "0.5.0", false, false, false, false)]
    [InlineData("0.5.0", "0.5.0", true, true, true, false)]
    [InlineData("0.4.0", "0.5.0", false, true, true, false)]
    [InlineData("0.6.0", "0.5.0", false, false, false, true)]
    public void RespectsRefreshPolicy(string candidate, string installed,
        bool identical, bool developCandidate, bool developOriginal, bool expected)
        => Assert.Equal(expected, AutomaticUpdatePolicy.ShouldUpgradeExtension(candidate, installed, identical, developCandidate, developOriginal));

    [Theory(DisplayName = "Automatic Selection Uses Release Order Instead Of Lexical Or Publication Order")]
    [InlineData("0.18.0-beta.9", "0.18.0-beta.10", -1)]
    [InlineData("0.18.0-rc.1", "0.18.0-beta.10", 1)]
    [InlineData("0.18.0", "0.18.0-rc.10", 1)]
    [InlineData("0.19.0-beta.1", "0.18.0", 1)]
    [InlineData("0.18.0", "0.18.0", 0)]
    public void UsesReleaseOrder(string candidate, string installed, int expected)
        => Assert.Equal(expected, Math.Sign(AutomaticUpdatePolicy.CompareVersions(candidate, installed)));

    [Fact(DisplayName = "Next Startup Cannot Apply In The Current Session And Manual Now Is Never Implicitly Started")]
    public void DefersStartupUpdates()
    {
        Assert.True(AutomaticUpdatePolicy.ShouldApplyPending(HostUpdateTiming.NextStartup, true, true));
        Assert.False(AutomaticUpdatePolicy.ShouldApplyPending(HostUpdateTiming.NextStartup, false, true));
        Assert.False(AutomaticUpdatePolicy.ShouldApplyPending(HostUpdateTiming.NextStartup, true, false));
        Assert.False(AutomaticUpdatePolicy.ShouldApplyPending(HostUpdateTiming.Now, true, true));
        Assert.False(AutomaticUpdatePolicy.ShouldApplyPending(HostUpdateTiming.Now, true, false));
        Assert.True(AutomaticUpdatePolicy.ShouldApplyPending(HostUpdateTiming.WhenIdle, false, false));
        Assert.False(AutomaticUpdatePolicy.ShouldApplyPending(HostUpdateTiming.WhenIdle, true, true));
        Assert.Throws<ArgumentException>(() => AutomaticUpdatePolicy.CompareVersions("0.18.00", "0.18.0"));
    }
}
