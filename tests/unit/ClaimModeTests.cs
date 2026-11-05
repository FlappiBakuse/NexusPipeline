using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ClaimModeTests
{
    [Theory(DisplayName = "A Verified Reference Hold Or Isolation Allows Other References And Blocks Mutations")]
    [InlineData(false)]
    [InlineData(true)]
    public void SeparatesClaimModes(bool isolation)
    {
        var index = new RecoveryIndex(); var operation = Guid.NewGuid();
        index.PublishVerified(0, 1, isolation ? [new("directory:a", Guid.NewGuid(), "recovery_pending") { Mode = ResourceClaimMode.Reference }] : []);
        if (!isolation) index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(operation, "directory:a", "edit_pending") { Mode = ResourceClaimMode.Reference }]);
        var lifecycle = new HostLifecycle(index, TimeProvider.System); var manager = new ResourceLeaseManager(lifecycle);
        using var reference = manager.TryAcquire(Guid.NewGuid(), [new("directory:a", ResourceClaimMode.Reference)]).Lease;
        Assert.NotNull(reference);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryAcquire(Guid.NewGuid(), [new("directory:a", ResourceClaimMode.Mutation)]).Denial);
        Assert.Equal("recovery_pending", lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).ReasonCode);
    }

    [Fact(DisplayName = "An Owned Reference Continuation Cannot Grant A Mutation Lease")]
    public void RejectsMutationPromotion()
    {
        var index = new RecoveryIndex(); index.PublishVerified(0, 1, []); var operation = Guid.NewGuid();
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(operation, "directory:a", "edit_pending") { Mode = ResourceClaimMode.Reference }]);
        var proof = index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, operation, new HashSet<string> { "directory:a" })!;
        var manager = new ResourceLeaseManager(new(index, TimeProvider.System));
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryContinue(proof, [new("directory:a", ResourceClaimMode.Mutation)]).Denial);
        using var reference = manager.TryContinue(proof, [new("directory:a", ResourceClaimMode.Reference)]).Lease;
        Assert.NotNull(reference);
    }

    [Fact(DisplayName = "Exit Draining Freezes The Accepted Claim Mode As Well As Its Identity And Resource Keys")]
    public void FreezesDrainClaims()
    {
        var index = new RecoveryIndex(); index.PublishVerified(0, 1, []); var operation = Guid.NewGuid();
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(operation, "directory:a", "edit_pending") { Mode = ResourceClaimMode.Reference }]);
        var lifecycle = new HostLifecycle(index, TimeProvider.System); var manager = new ResourceLeaseManager(lifecycle);
        lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(operation, "directory:a", "edit_pending") { Mode = ResourceClaimMode.Mutation }]);
        var proof = index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, operation, new HashSet<string> { "directory:a" })!;
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryContinue(proof, [new("directory:a", ResourceClaimMode.Mutation)]).Denial);
        using var reference = manager.TryContinue(proof, [new("directory:a", ResourceClaimMode.Reference)]).Lease;
        Assert.NotNull(reference);
    }
}
