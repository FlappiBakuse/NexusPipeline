using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class RecoveryContinuationTests
{
    [Fact(DisplayName = "A Continuation Claims Only Its Frozen Owned Resources While Ordinary Admission Remains Blocked")]
    public void ScopesOwnedClaims()
    {
        var (index, _, manager) = Ready(); var operation = Guid.NewGuid();
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(operation, "slot:a", "edit_pending"), new(operation, "slot:b", "edit_pending")]);
        var keys = new HashSet<string> { "slot:a" };
        var continuation = Assert.IsType<RecoveryContinuation>(index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, operation, keys));
        keys.Add("slot:b");
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryAcquire(operation, [new("slot:a", ResourceClaimMode.Mutation)]).Denial);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryContinue(continuation, [new("slot:b", ResourceClaimMode.Mutation)]).Denial);
        using var lease = manager.TryContinue(continuation, [new("slot:a", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(lease); Assert.Equal(operation, lease.OperationId);
        Assert.Null(index.AuthorizeContinuation(RecoveryHoldOwner.StartupRegistration, operation, new HashSet<string> { "slot:a" }));
        Assert.Null(index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, Guid.NewGuid(), new HashSet<string> { "slot:a" }));
    }

    [Theory(DisplayName = "Every Continuation Revalidates Its Current Ownership And Verified Recovery Before Taking Any Lease")]
    [InlineData("removed")]
    [InlineData("foreign")]
    [InlineData("owner")]
    [InlineData("isolated")]
    [InlineData("unknown")]
    public void RechecksCurrentOwnership(string fault)
    {
        var (index, _, manager) = Ready(); var operation = Guid.NewGuid();
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(operation, "slot:a", "edit_pending")]);
        var continuation = index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, operation, new HashSet<string> { "slot:a" })!;
        if (fault == "removed") index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
        if (fault == "foreign") index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(Guid.NewGuid(), "slot:a", "edit_pending")]);
        if (fault == "owner")
        {
            index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
            index.UpdateResourceHolds(RecoveryHoldOwner.StartupRegistration,
                [new(operation, "slot:a", "startup_pending") { Owner = RecoveryHoldOwner.ConfigurationOperations }]);
        }
        if (fault == "isolated") index.PublishVerified(index.Current.Revision, index.Current.Revision + 1, [new("slot:a", Guid.NewGuid(), "writer_unknown")]);
        if (fault == "unknown") index.BlockUnknown("unverified");
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryContinue(continuation, [new("slot:a", ResourceClaimMode.Mutation)]).Denial);
    }

    [Fact(DisplayName = "A Continuation From Another Recovery Index Cannot Authorize The Same Ids And Resource Names")]
    public void RejectsForeignIndexes()
    {
        var (first, _, _) = Ready(); var (second, _, manager) = Ready(); var operation = Guid.NewGuid();
        first.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(operation, "slot:a", "pending")]);
        second.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(operation, "slot:a", "pending")]);
        var continuation = first.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, operation, new HashSet<string> { "slot:a" })!;
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryContinue(continuation, [new("slot:a", ResourceClaimMode.Mutation)]).Denial);
    }

    [Fact(DisplayName = "Continuations Use The Same Conflict Accounting And Retain The Lifecycle Write Until Disposal")]
    public void PreservesConflictAccounting()
    {
        var (index, lifecycle, manager) = Ready(); var operation = Guid.NewGuid();
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(operation, "slot:a", "pending")]);
        var continuation = index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, operation, new HashSet<string> { "slot:a" })!;
        using var lease = manager.TryContinue(continuation, [new("slot:a", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(lease);
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryContinue(continuation, [new("slot:a", ResourceClaimMode.Reference)]).Denial);
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
        var closing = Guid.NewGuid();
        Assert.Equal("accepted_writes_pending", lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        lease.Dispose(); lease.Dispose();
        Assert.NotNull(lifecycle.RequestClose(closing, Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "Exit Draining Allows Only Intents Already Held At Its Freeze And Waits For Their Completion")]
    public void FreezesDrainIntents()
    {
        var (index, lifecycle, manager) = Ready(); var accepted = Guid.NewGuid(); var late = Guid.NewGuid();
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(accepted, "slot:a", "pending")]);
        var closing = Guid.NewGuid();
        Assert.Equal("recovery_pending", lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(accepted, "slot:a", "pending"), new(late, "slot:b", "pending"),
            new(accepted, "slot:expanded", "pending")]);
        var expandedProof = index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, accepted, new HashSet<string> { "slot:expanded" })!;
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryContinue(expandedProof, [new("slot:expanded", ResourceClaimMode.Mutation)]).Denial);
        var lateProof = index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, late, new HashSet<string> { "slot:b" })!;
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryContinue(lateProof, [new("slot:b", ResourceClaimMode.Mutation)]).Denial);
        var acceptedProof = index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, accepted, new HashSet<string> { "slot:a" })!;
        using var lease = manager.TryContinue(acceptedProof, [new("slot:a", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(lease);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryAcquire(Guid.NewGuid(), [new("unrelated", ResourceClaimMode.Reference)]).Denial);
        Assert.Equal("recovery_pending", lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
        Assert.Equal("accepted_writes_pending", lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        lease.Dispose(); Assert.NotNull(lifecycle.RequestClose(closing, Guid.NewGuid()).Permit);
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(accepted, "slot:a", "pending")]);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryContinue(acceptedProof, [new("slot:a", ResourceClaimMode.Mutation)]).Denial);
    }

    [Fact(DisplayName = "Shared Definition Holds Cannot Be Ignored But Disjoint Owned Slots Can Finish Independently")]
    public void RespectsSharedHolds()
    {
        var (index, _, manager) = Ready(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(first, "definitions", "pending"), new(first, "slot:a", "pending"),
            new(second, "definitions", "pending"), new(second, "slot:b", "pending")]);
        Assert.Null(index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, first, new HashSet<string> { "definitions" }));
        using var a = manager.TryContinue(index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, first, new HashSet<string> { "slot:a" })!,
            [new("slot:a", ResourceClaimMode.Mutation)]).Lease;
        using var b = manager.TryContinue(index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, second, new HashSet<string> { "slot:b" })!,
            [new("slot:b", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(a); Assert.NotNull(b);
    }

    [Fact(DisplayName = "A Stale Hold Publication Cannot Remove Newer Accepted Holds Or Change Their Subsystem Owner")]
    public void RejectsStalePublication()
    {
        var (index, _, _) = Ready(); var observed = index.Current.Revision; var operation = Guid.NewGuid();
        index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations,
            [new(operation, "slot:a", "pending") { Owner = RecoveryHoldOwner.StartupRegistration }]);
        Assert.False(index.TryUpdateHolds(RecoveryHoldOwner.ConfigurationOperations, observed, []));
        var hold = Assert.Single(index.Current.ResourceHolds);
        Assert.Equal(operation, hold.OperationId); Assert.Equal(RecoveryHoldOwner.ConfigurationOperations, hold.Owner);
    }

    private static (RecoveryIndex, HostLifecycle, ResourceLeaseManager) Ready()
    {
        var index = new RecoveryIndex(); index.PublishVerified(0, 1, []);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        return (index, lifecycle, new(lifecycle));
    }
}
