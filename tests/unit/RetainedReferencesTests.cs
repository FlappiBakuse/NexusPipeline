using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class RetainedReferencesTests
{
    private static readonly ResourceClaim Extension = new("extension:fixture", ResourceClaimMode.Reference);
    private static readonly ResourceClaim Image = new("filesystem:object:fixture", ResourceClaimMode.Reference);
    private static readonly ResourceClaim Script = new("automation:fixture", ResourceClaimMode.Mutation);

    [Fact(DisplayName = "Retaining Exact References Releases The Parents Script Mutation But Still Blocks Extension And Image Mutation")]
    public void PreservesExactReferences()
    {
        var (lifecycle, manager) = Ready(); var operation = Guid.NewGuid();
        using var parent = Acquire(manager, operation);
        using var retained = parent.RetainReferences(manager, operation, [Extension, Image]);
        parent.Dispose();
        using (var nextScript = manager.TryAcquire(Guid.NewGuid(), [Script]).Lease) Assert.NotNull(nextScript);
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [Extension with { Mode = ResourceClaimMode.Mutation }]).Denial);
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [Image with { Mode = ResourceClaimMode.Mutation }]).Denial);
        retained.Dispose(); retained.Dispose();
        using var update = manager.TryAcquire(Guid.NewGuid(), [Extension with { Mode = ResourceClaimMode.Mutation }]).Lease;
        Assert.NotNull(update); update.Dispose();
        Assert.NotNull(lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
    }

    [Theory(DisplayName = "Retention Cannot Widen Or Downgrade The Actual Source Or Use A Foreign Or Disposed Root")]
    [InlineData("manager")]
    [InlineData("operation")]
    [InlineData("missing")]
    [InlineData("mutation")]
    [InlineData("downgrade")]
    [InlineData("disposed")]
    public void RejectsChangedRetention(string fault)
    {
        var (_, manager) = Ready(); var (_, foreign) = Ready(); var operation = Guid.NewGuid();
        using var parent = Acquire(manager, operation);
        IReadOnlyList<ResourceClaim> required = fault switch
        {
            "missing" => [new("not-owned", ResourceClaimMode.Reference)],
            "mutation" => [Extension with { Mode = ResourceClaimMode.Mutation }],
            "downgrade" => [Script with { Mode = ResourceClaimMode.Reference }], _ => [Extension]
        };
        if (fault == "disposed") parent.Dispose();
        var failure = Record.Exception(() => parent.RetainReferences(fault == "manager" ? foreign : manager,
            fault == "operation" ? Guid.NewGuid() : operation, required));
        if (fault == "disposed") Assert.IsType<ObjectDisposedException>(failure);
        else Assert.IsType<InvalidOperationException>(failure);
        parent.Dispose();
        using var update = manager.TryAcquire(Guid.NewGuid(), [Extension with { Mode = ResourceClaimMode.Mutation }]).Lease;
        Assert.NotNull(update);
    }

    [Fact(DisplayName = "Passive References Do Not Grant New Effects Or Block Normal Close After The Accepted Write Ends")]
    public void SeparatesPassiveReferences()
    {
        var (lifecycle, manager) = Ready(); var operation = Guid.NewGuid(); var session = Guid.NewGuid();
        using var parent = Acquire(manager, operation);
        Assert.Null(lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        using var retained = parent.RetainReferences(manager, operation, [Extension, Image]);
        parent.Dispose();
        retained.VerifyHeld(manager, operation);
        Assert.NotNull(lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryAcquire(Guid.NewGuid(), [Script]).Denial);
        Assert.Equal(2, retained.Use(() => retained.Claims.Count));
    }

    [Fact(DisplayName = "Disposal During An Accepted Use Keeps The Reference Until That Actual Use Returns")]
    public async Task RetainsActiveUses()
    {
        var (_, manager) = Ready(); var operation = Guid.NewGuid();
        using var parent = Acquire(manager, operation); var retained = parent.RetainReferences(manager, operation, [Extension]);
        parent.Dispose();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(() => retained.Use(() =>
        {
            entered.SetResult(); release.Task.GetAwaiter().GetResult(); return 7;
        }));
        await entered.Task;
        try
        {
            retained.Dispose();
            Assert.Throws<ObjectDisposedException>(() => retained.VerifyHeld(manager, operation));
            Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [Extension with { Mode = ResourceClaimMode.Mutation }]).Denial);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(7, await work);
        using var update = manager.TryAcquire(Guid.NewGuid(), [Extension with { Mode = ResourceClaimMode.Mutation }]).Lease;
        Assert.NotNull(update);
    }

    private static ResourceOperationLease Acquire(ResourceLeaseManager manager, Guid operation) =>
        Assert.IsType<ResourceOperationLease>(manager.TryAcquire(operation, [Extension, Image, Script]).Lease);
    private static (HostLifecycle, ResourceLeaseManager) Ready()
    {
        var index = new RecoveryIndex(); index.PublishVerified(0, 1, []);
        var lifecycle = new HostLifecycle(index, TimeProvider.System); return (lifecycle, new(lifecycle));
    }
}
