using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ResourceLeaseTests
{
    [Fact(DisplayName = "References May Coexist While Mutation Waits For Every Reference To Release")]
    public void WaitsForReferences()
    {
        var (_, _, manager) = Ready();
        var operation = Guid.NewGuid();
        using var first = Accepted(manager, operation, new("blob:a", ResourceClaimMode.Reference));
        using var second = Accepted(manager, Guid.NewGuid(), new("blob:a", ResourceClaimMode.Reference));
        Assert.Equal(operation, first.OperationId);
        Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [new("blob:a", ResourceClaimMode.Mutation)]).Denial);
        first.Dispose();
        Assert.False(first.IsActive);
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [new("blob:a", ResourceClaimMode.Mutation)]).Denial);
        second.Dispose();
        using var mutation = Accepted(manager, Guid.NewGuid(), new("blob:a", ResourceClaimMode.Mutation));
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [new("blob:a", ResourceClaimMode.Reference)]).Denial);
    }

    [Fact(DisplayName = "Conflicting Concurrent Multi Resource Requests Never Retain A Partial Claim Or Lifecycle Write")]
    public async Task KeepsAtomicAdmission()
    {
        var (_, lifecycle, manager) = Ready();
        using var held = Accepted(manager, Guid.NewGuid(), new("blob:a", ResourceClaimMode.Mutation));
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => manager.TryAcquire(Guid.NewGuid(),
            [new("blob:a", ResourceClaimMode.Mutation), new("blob:b", ResourceClaimMode.Mutation)]))));
        Assert.All(results, result => { Assert.Null(result.Lease); Assert.Equal(ResourceLeaseDenial.Conflict, result.Denial); });
        using (Accepted(manager, Guid.NewGuid(), new("blob:b", ResourceClaimMode.Mutation))) { }
        held.Dispose();
        Assert.NotNull(lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "The Resource Lease Retains Its Frozen Claims And Only One Disposal Releases Them")]
    public void FreezesLeaseClaims()
    {
        var (_, _, manager) = Ready();
        var claims = new List<ResourceClaim> { new("blob:a", ResourceClaimMode.Reference) };
        var admission = manager.TryAcquire(Guid.NewGuid(), claims);
        using var lease = Assert.IsType<ResourceOperationLease>(admission.Lease);
        claims[0] = new("blob:b", ResourceClaimMode.Mutation);
        claims.Clear();
        Assert.Equal("blob:a", Assert.Single(lease.Claims).ResourceKey);
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [new("blob:a", ResourceClaimMode.Mutation)]).Denial);
        lease.Dispose();
        lease.Dispose();
        using var next = Accepted(manager, Guid.NewGuid(), new("blob:a", ResourceClaimMode.Mutation));
    }

    [Fact(DisplayName = "Unknown Recovery And Related Isolation Reject Acquisition Without Blocking Unrelated Resources")]
    public void ScopesRecoveryIsolation()
    {
        var index = new RecoveryIndex();
        var manager = new ResourceLeaseManager(new(index, TimeProvider.System));
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryAcquire(Guid.NewGuid(), [new("blob:a", ResourceClaimMode.Reference)]).Denial);
        index.PublishVerified(0, 1, [new("blob:a", Guid.NewGuid(), "recovery_pending")]);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryAcquire(Guid.NewGuid(), [new("blob:a", ResourceClaimMode.Mutation)]).Denial);
        using var unrelated = Accepted(manager, Guid.NewGuid(), new("blob:b", ResourceClaimMode.Mutation));
        index.BlockUnknown("external_change_unverified");
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryAcquire(Guid.NewGuid(), [new("blob:c", ResourceClaimMode.Reference)]).Denial);
    }

    [Fact(DisplayName = "Host Closure Waits For The Resource Lease And Freezes All Later Requests")]
    public void DrainsActiveLeases()
    {
        var (_, lifecycle, manager) = Ready();
        using var lease = Accepted(manager, Guid.NewGuid(), new("blob:a", ResourceClaimMode.Reference));
        var session = Guid.NewGuid();
        Assert.Equal("accepted_writes_pending", lifecycle.RequestClose(session, Guid.NewGuid()).ReasonCode);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryAcquire(Guid.NewGuid(), [new("blob:b", ResourceClaimMode.Mutation)]).Denial);
        lease.Dispose();
        Assert.NotNull(lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryAcquire(Guid.NewGuid(), [new("blob:a", ResourceClaimMode.Reference)]).Denial);
    }

    [Theory(DisplayName = "Invalid Resource Requests Cannot Leak A Write Lease Or Acquire Any Resource")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("mode")]
    [InlineData("key")]
    [InlineData("length")]
    [InlineData("whitespace")]
    [InlineData("control")]
    [InlineData("operation")]
    public void RejectsInvalidClaims(string fault)
    {
        var (_, lifecycle, manager) = Ready();
        IReadOnlyList<ResourceClaim> claims = fault switch
        {
            "empty" => [], "duplicate" => [new("blob:a", ResourceClaimMode.Reference), new("blob:a", ResourceClaimMode.Mutation)],
            "mode" => [new("blob:a", (ResourceClaimMode)17)], "key" => [new("", ResourceClaimMode.Reference)],
            "length" => [new(new string('a', 513), ResourceClaimMode.Reference)],
            "whitespace" => [new("blob:a ", ResourceClaimMode.Reference)], "control" => [new("blob:\0a", ResourceClaimMode.Reference)],
            _ => [new("blob:a", ResourceClaimMode.Reference)]
        };
        Assert.Throws<ArgumentException>(() => manager.TryAcquire(fault == "operation" ? Guid.Empty : Guid.NewGuid(), claims));
        Assert.NotNull(lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
    }

    private static ResourceOperationLease Accepted(ResourceLeaseManager manager, Guid operation, ResourceClaim claim)
    {
        var result = manager.TryAcquire(operation, [claim]);
        Assert.Equal(ResourceLeaseDenial.None, result.Denial);
        return Assert.IsType<ResourceOperationLease>(result.Lease);
    }

    private static (RecoveryIndex, HostLifecycle, ResourceLeaseManager) Ready()
    {
        var index = new RecoveryIndex();
        index.PublishVerified(0, 1, []);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        return (index, lifecycle, new(lifecycle));
    }
}
