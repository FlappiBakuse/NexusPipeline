using NexusPipeline.Core.Configuration;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class RecoveryIndexTests
{
    [Fact(DisplayName = "Startup And Unknown State Block Even An Empty Requested Resource Set")]
    public void BlocksUnknownState()
    {
        var index = new RecoveryIndex();
        Assert.True(index.IsBlocked(new HashSet<string>(), out var initial));
        Assert.Equal(0, initial);
        index.PublishVerified(0, 1, []);
        Assert.False(index.IsBlocked(new HashSet<string> { "unrelated" }, out _));
        var blocked = index.BlockUnknown("unknown_journal");
        Assert.True(index.IsBlocked(new HashSet<string>(), out var current));
        Assert.Equal(blocked.Revision, current);
        Assert.Equal("unknown_journal", index.Current.GlobalBlockReason);
    }

    [Theory(DisplayName = "A Public Scan From Before Invalidation Or A Hold Update Cannot Reopen Admission")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsStaleScans(bool holdUpdate)
    {
        var index = new RecoveryIndex();
        var observed = index.Current;
        if (holdUpdate) index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations,
            [new(Guid.NewGuid(), "slot:changed", "configuration_operation_pending")]);
        else index.BlockUnknown("external_change");
        var current = index.Current;
        Assert.Throws<InvalidOperationException>(() => index.PublishVerified(observed.Revision, 2, []));
        Assert.False(index.Current.IsReady);
        Assert.Equal(current.Revision, index.Current.Revision);
        Assert.Equal(current.GlobalBlockReason, index.Current.GlobalBlockReason);
        Assert.Equal(current.ResourceHolds, index.Current.ResourceHolds);
    }

    [Fact(DisplayName = "Published Isolation Is Immutable And Only Blocks The Affected Resources")]
    public void FreezesPublishedIsolation()
    {
        var index = new RecoveryIndex();
        var supplied = new List<ResourceIsolation> { new("config-a", Guid.NewGuid(), "writer_unknown") };
        index.PublishVerified(0, 1, supplied);
        supplied.Clear();
        Assert.True(index.IsBlocked(new HashSet<string> { "config-a" }, out var revision));
        Assert.Equal(1, revision);
        Assert.False(index.IsBlocked(new HashSet<string> { "config-b" }, out _));
        Assert.Single(index.Current.Isolations);
    }
}
