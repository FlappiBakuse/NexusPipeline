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

    [Fact(DisplayName = "Idle Maintenance Does Not Begin Draining While An Actual Write Is Still Accepted")]
    public void WaitsForWrites()
    {
        var recovery = new RecoveryIndex(); recovery.PublishVerified(0, 1, []);
        var lifecycle = new HostLifecycle(recovery, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        using var work = resources.TryAcquire(Guid.NewGuid(), [new("config", ResourceClaimMode.Mutation)]).Lease!;
        Assert.NotNull(work);
        Assert.Null(lifecycle.BeginCompletionDrain(Guid.NewGuid(), onlyWhenIdle: true));
        Assert.Equal(HostLifecyclePhase.Running, lifecycle.Phase);
        using var independent = resources.TryAcquire(Guid.NewGuid(), [new("other", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(independent);
        independent.Dispose(); work.Dispose();
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid(), onlyWhenIdle: true);
        Assert.NotNull(drain); Assert.True(drain.TryReserve());
        Assert.Null(resources.TryAcquire(Guid.NewGuid(), [new("new", ResourceClaimMode.Mutation)]).Lease);
    }

    [Fact(DisplayName = "An Actual Task Admission And Idle Maintenance Compete Within The Same Lifecycle Gate")]
    public async Task SerializesMaintenanceAdmission()
    {
        var recovery = new RecoveryIndex(); recovery.PublishVerified(0, 1, []);
        var lifecycle = new HostLifecycle(recovery, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        using var barrier = new Barrier(2);
        var write = Task.Run(() => { barrier.SignalAndWait(); return resources.TryAcquire(Guid.NewGuid(), [new("config", ResourceClaimMode.Mutation)]); });
        var maintenance = Task.Run(() => { barrier.SignalAndWait(); return lifecycle.BeginCompletionDrain(Guid.NewGuid(), onlyWhenIdle: true); });
        await Task.WhenAll(write, maintenance);
        using var admitted = (await write).Lease; using var drain = await maintenance;
        Assert.NotEqual(admitted is not null, drain is not null);
        if (drain is not null) Assert.True(drain.TryReserve());
        else Assert.Equal(HostLifecyclePhase.Running, lifecycle.Phase);
    }
}
