using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class AcceptedRunTests
{
    [Fact(DisplayName = "An Accepted Run Keeps Completion Waiting Without Pinning Its Attempt Resources And Can Readmit Only Its Original Scope")]
    public void PreservesAcceptedClaims()
    {
        var index = Ready(); var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle); var first = Guid.NewGuid();
        using var original = resources.TryAcquire(first,
            [new("config", ResourceClaimMode.Mutation), new("package", ResourceClaimMode.Reference)]).Lease!;
        using var run = original.AcceptRun(resources, first, Guid.NewGuid());
        original.Dispose();
        using (var unrelated = resources.TryAcquire(Guid.NewGuid(), [new("config", ResourceClaimMode.Mutation)]).Lease)
            Assert.NotNull(unrelated);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid()); Assert.NotNull(drain);
        Assert.False(drain.TryReserve());
        Assert.Null(resources.TryAcquire(Guid.NewGuid(), [new("unrelated", ResourceClaimMode.Mutation)]).Lease);
        Assert.Null(resources.TryContinueRun(run, Guid.NewGuid(), [new("outside", ResourceClaimMode.Mutation)]).Lease);
        Assert.Null(resources.TryContinueRun(run, Guid.NewGuid(), [new("package", ResourceClaimMode.Mutation)]).Lease);
        using var next = resources.TryContinueRun(run, Guid.NewGuid(),
            [new("config", ResourceClaimMode.Mutation), new("package", ResourceClaimMode.Reference)]).Lease;
        Assert.NotNull(next); Assert.False(drain.TryReserve());
        next.Dispose(); run.Dispose(); Assert.False(run.IsActive);
        Assert.True(drain.TryReserve());
        Assert.Null(resources.TryContinueRun(run, Guid.NewGuid(), [new("config", ResourceClaimMode.Mutation)]).Lease);
    }

    [Fact(DisplayName = "A Run Permit Cannot Cross Its Real Coordinator Or Override Current Recovery Isolation")]
    public void RejectsForeignPermits()
    {
        var index = Ready(); var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle); var foreign = new ResourceLeaseManager(lifecycle);
        var operation = Guid.NewGuid();
        using var original = resources.TryAcquire(operation, [new("config", ResourceClaimMode.Mutation)]).Lease!;
        Assert.Throws<InvalidOperationException>(() => original.AcceptRun(foreign, operation, Guid.NewGuid()));
        using var run = original.AcceptRun(resources, operation, Guid.NewGuid()); original.Dispose();
        Assert.Null(foreign.TryContinueRun(run, Guid.NewGuid(), [new("config", ResourceClaimMode.Mutation)]).Lease);
        index.PublishVerified(1, 2, [new("config", Guid.NewGuid(), "writer_unknown")]);
        Assert.Null(resources.TryContinueRun(run, Guid.NewGuid(), [new("config", ResourceClaimMode.Mutation)]).Lease);
        index.PublishVerified(2, 3, []);
        using var current = resources.TryContinueRun(run, Guid.NewGuid(), [new("config", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(current);
    }

    private static RecoveryIndex Ready()
    { var value = new RecoveryIndex(); value.PublishVerified(0, 1, []); return value; }
}
