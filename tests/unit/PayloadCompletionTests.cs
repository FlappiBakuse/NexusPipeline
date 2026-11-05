using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class PayloadCompletionTests
{
    [Theory(DisplayName = "A Host Payload Cannot Acquire Its Fixed Completion Claims Before Its Own Effect Was Entered")]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresEnteredEffect(bool reserved)
    {
        var lifecycle = NewLifecycle(); var resources = new ResourceLeaseManager(lifecycle);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid())!;
        if (reserved) Assert.True(drain.TryReserve());
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, resources.TryCompletePayload(drain).Denial);
        Assert.True(drain.TryReserve()); Assert.True(drain.TryEnterEffect());
        using var actual = resources.TryCompletePayload(drain).Lease;
        Assert.NotNull(actual);
        Assert.Equal(drain.OperationId, actual.OperationId);
        Assert.Equal(new[] { "blobs:content", "maintenance:feed", "maintenance:host-payload" }, actual.Claims.Select(claim => claim.ResourceKey));
        Assert.Equal(new[] { ResourceClaimMode.Reference, ResourceClaimMode.Reference, ResourceClaimMode.Mutation }, actual.Claims.Select(claim => claim.Mode));
        actual.Dispose(); Assert.True(drain.ReleaseFinishedEffect());
    }

    [Fact(DisplayName = "The Completion Drain Cannot Be Consumed By Another Lifecycle Or Consumed Twice After Its Lease Ends")]
    public void BindsCompletionDrain()
    {
        var lifecycle = NewLifecycle(); var resources = new ResourceLeaseManager(lifecycle);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid())!;
        Assert.True(drain.TryReserve()); Assert.True(drain.TryEnterEffect());
        Assert.Null(new ResourceLeaseManager(NewLifecycle()).TryCompletePayload(drain).Lease);
        using var actual = resources.TryCompletePayload(drain).Lease!;
        Assert.NotNull(actual); Assert.False(drain.ReleaseFinishedEffect());
        Assert.Null(resources.TryCompletePayload(drain).Lease);
        actual.Dispose();
        Assert.Null(resources.TryCompletePayload(drain).Lease);
        Assert.Null(resources.TryAcquire(Guid.NewGuid(), [new("ordinary", ResourceClaimMode.Mutation)]).Lease);
        Assert.True(drain.ReleaseFinishedEffect());
        using var ordinary = resources.TryAcquire(Guid.NewGuid(), [new("ordinary", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(ordinary);
    }

    [Fact(DisplayName = "An Actual Preparation Reference Must Be Released Before Completion Can Reserve Or Take Its Payload Claims")]
    public void ReleasesPreparationReferences()
    {
        var lifecycle = NewLifecycle(); var resources = new ResourceLeaseManager(lifecycle);
        using var preparation = resources.TryAcquire(Guid.NewGuid(), [new("maintenance:host-payload", ResourceClaimMode.Reference)]).Lease!;
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid())!;
        Assert.False(drain.TryReserve()); Assert.False(drain.TryEnterEffect());
        Assert.Null(resources.TryCompletePayload(drain).Lease);
        preparation.Dispose();
        Assert.True(drain.TryReserve()); Assert.True(drain.TryEnterEffect());
        using var actual = resources.TryCompletePayload(drain).Lease;
        Assert.NotNull(actual);
        actual.Dispose(); Assert.True(drain.ReleaseFinishedEffect());
    }

    [Fact(DisplayName = "Two Concurrent Consumers Of One Entered Completion Drain Can Obtain Only One Actual Payload Lease")]
    public async Task EntersSingleLease()
    {
        var lifecycle = NewLifecycle(); var resources = new ResourceLeaseManager(lifecycle);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid())!;
        Assert.True(drain.TryReserve()); Assert.True(drain.TryEnterEffect());
        using var barrier = new Barrier(2);
        var first = Task.Run(() => { barrier.SignalAndWait(); return resources.TryCompletePayload(drain); });
        var second = Task.Run(() => { barrier.SignalAndWait(); return resources.TryCompletePayload(drain); });
        var results = await Task.WhenAll(first, second);
        using var actual = Assert.Single(results, result => result.Lease is not null).Lease!;
        Assert.Single(results, result => result.Denial == ResourceLeaseDenial.HostUnavailable);
        actual.Dispose(); Assert.True(drain.ReleaseFinishedEffect());
    }

    [Theory(DisplayName = "A Recovery Change After Effect Entry Still Blocks The Specific Host Payload Completion Lease")]
    [InlineData(false)]
    [InlineData(true)]
    public void RechecksCompletionRecovery(bool isolation)
    {
        var recovery = new RecoveryIndex(); recovery.PublishVerified(0, 1, []);
        var lifecycle = new HostLifecycle(recovery, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid())!;
        Assert.True(drain.TryReserve()); Assert.True(drain.TryEnterEffect());
        if (isolation) recovery.PublishVerified(1, 2, [new("outside", Guid.NewGuid(), "unknown-writer")]);
        else recovery.BlockUnknown("unverified");
        Assert.Null(resources.TryCompletePayload(drain).Lease);
        Assert.True(drain.ReleaseFinishedEffect());
    }

    [Fact(DisplayName = "An Outstanding Borrow Keeps Completion Closed After Its Public Payload Lease Is Disposed")]
    public void RetainsOutstandingBorrows()
    {
        var lifecycle = NewLifecycle(); var resources = new ResourceLeaseManager(lifecycle);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid())!;
        Assert.True(drain.TryReserve()); Assert.True(drain.TryEnterEffect());
        using var actual = resources.TryCompletePayload(drain).Lease!;
        using var borrow = actual.Borrow(resources, drain.OperationId, actual.Claims);
        actual.Dispose();
        Assert.False(drain.ReleaseFinishedEffect());
        Assert.True(borrow.Use(() => lifecycle.Phase == HostLifecyclePhase.ClosePermitted));
        borrow.Dispose(); Assert.True(drain.ReleaseFinishedEffect());
    }

    private static HostLifecycle NewLifecycle()
    {
        var recovery = new RecoveryIndex(); recovery.PublishVerified(0, 1, []);
        return new(recovery, TimeProvider.System);
    }
}
