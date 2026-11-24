using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class UpdateFeedTests
{
    [Theory(DisplayName = "Host Feed Floor And Watermark Are Both Required Before Accepting The Original Digest")]
    [InlineData(4, 5, 0, false)]
    [InlineData(5, 5, 0, true)]
    [InlineData(6, 1, 7, false)]
    [InlineData(7, 1, 7, true)]
    [InlineData(8, 1, 7, true)]
    public void RequiresFeedFloors(long proposed, long floor, long accepted, bool allowed)
    {
        var identity = new UpdateFeedIdentity(HostUpdateChannel.Stable, proposed, new('a', 64));
        var head = accepted == 0 ? null : new UpdateFeedIdentity(identity.Channel, accepted, identity.Sha256);
        if (allowed) UpdateFeedPolicy.Require(identity, floor, head);
        else Assert.Throws<InvalidOperationException>(() => UpdateFeedPolicy.Require(identity, floor, head));
        if (accepted == proposed)
            Assert.Throws<InvalidOperationException>(() => UpdateFeedPolicy.Require(identity, floor, head! with { Sha256 = new('b', 64) }));
    }
}
