using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ResourceBorrowTests
{
    private static readonly ResourceClaim Mutation = new("configuration:slot", ResourceClaimMode.Mutation);

    [Fact(DisplayName = "An Active Lease From Another Manager Cannot Authorize The Same Operation And Claims")]
    public void RejectsForeignManagers()
    {
        var (_, actual) = Ready(); var (_, foreign) = Ready(); var operation = Guid.NewGuid();
        using var actualLease = Acquire(actual, operation);
        using var foreignLease = Acquire(foreign, operation);
        Assert.Equal("resource_operation_borrow_unbound", Assert.Throws<InvalidOperationException>(() =>
            foreignLease.Borrow(actual, operation, [Mutation])).Message);
        using var accepted = actualLease.Borrow(actual, operation, [Mutation]);
        accepted.VerifyHeld();
    }

    [Theory(DisplayName = "Borrowing Requires The Live Root Operation And Every Requested Claim Mode")]
    [InlineData("operation")]
    [InlineData("missing")]
    [InlineData("mode")]
    [InlineData("disposed")]
    public void RequiresRootClaims(string fault)
    {
        var (lifecycle, manager) = Ready(); var operation = Guid.NewGuid();
        using var root = Acquire(manager, operation, fault == "mode" ? new(Mutation.ResourceKey, ResourceClaimMode.Reference) : Mutation);
        if (fault == "disposed") root.Dispose();
        var required = fault == "missing" ? new ResourceClaim("unowned", ResourceClaimMode.Reference) : Mutation;
        if (fault == "disposed") Assert.Throws<ObjectDisposedException>(() => root.Borrow(manager, operation, [required]));
        else Assert.Equal("resource_operation_borrow_unbound", Assert.Throws<InvalidOperationException>(() =>
            root.Borrow(manager, fault == "operation" ? Guid.NewGuid() : operation, [required])).Message);
        root.Dispose();
        Assert.NotNull(lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
    }

    [Theory(DisplayName = "Invalid Borrow Requirements Do Not Extend The Root Lifetime")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("mode")]
    [InlineData("key")]
    [InlineData("whitespace")]
    [InlineData("control")]
    [InlineData("operation")]
    public void RejectsInvalidBorrows(string fault)
    {
        var (lifecycle, manager) = Ready(); var operation = Guid.NewGuid();
        using var root = Acquire(manager, operation);
        IReadOnlyList<ResourceClaim> required = fault switch
        {
            "empty" => [], "duplicate" => [Mutation, Mutation], "mode" => [new(Mutation.ResourceKey, (ResourceClaimMode)42)],
            "key" => [new("", ResourceClaimMode.Reference)], "whitespace" => [new("slot ", ResourceClaimMode.Reference)],
            "control" => [new("slot\0", ResourceClaimMode.Reference)], _ => [Mutation]
        };
        Assert.Throws<ArgumentException>(() => root.Borrow(manager, fault == "operation" ? Guid.Empty : operation, required));
        root.Dispose();
        Assert.NotNull(lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "Root Disposal Retains Claims And Host Write Until The Last Accepted Child Borrow Ends")]
    public void RetainsBorrowedClaims()
    {
        var (lifecycle, manager) = Ready(); var operation = Guid.NewGuid();
        using var root = Acquire(manager, operation);
        var requirements = new List<ResourceClaim> { new(Mutation.ResourceKey, ResourceClaimMode.Reference) };
        using var first = root.Borrow(manager, operation, requirements);
        requirements.Clear();
        using var second = root.Borrow(manager, operation, [Mutation]);
        root.Dispose(); root.Dispose();
        Assert.False(root.IsActive);
        Assert.Throws<ObjectDisposedException>(() => root.Borrow(manager, operation, [Mutation]));
        first.VerifyHeld(); second.VerifyHeld();
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [Mutation]).Denial);
        var closeSession = Guid.NewGuid();
        Assert.Equal("accepted_writes_pending", lifecycle.RequestClose(closeSession, Guid.NewGuid()).ReasonCode);
        first.Dispose(); first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.VerifyHeld());
        Assert.Null(lifecycle.RequestClose(closeSession, Guid.NewGuid()).Permit);
        Assert.Equal(19, second.Use(() => 19));
        second.Dispose();
        Assert.NotNull(lifecycle.RequestClose(closeSession, Guid.NewGuid()).Permit);
    }

    [Theory(DisplayName = "An Already Entered Borrow Use Keeps Resources Until Its Callback Returns Or Throws")]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsActiveUses(bool fails)
    {
        var (lifecycle, manager) = Ready(); var operation = Guid.NewGuid();
        using var root = Acquire(manager, operation);
        using var borrow = root.Borrow(manager, operation, [Mutation]);
        var closeSession = Guid.NewGuid();
        int Action()
        {
            borrow.Dispose(); root.Dispose();
            Assert.Throws<ObjectDisposedException>(() => borrow.VerifyHeld());
            Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [Mutation]).Denial);
            Assert.Null(lifecycle.RequestClose(closeSession, Guid.NewGuid()).Permit);
            if (fails) throw new InvalidOperationException("fixture_failure");
            return 31;
        }
        if (fails) Assert.Equal("fixture_failure", Assert.Throws<InvalidOperationException>(() => borrow.Use(Action)).Message);
        else Assert.Equal(31, borrow.Use(Action));
        Assert.Throws<ObjectDisposedException>(() => borrow.Use(() => 0));
        Assert.NotNull(lifecycle.RequestClose(closeSession, Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "Concurrent Borrow And Root Disposal Release Every Claim And Lifecycle Write Exactly Once")]
    public async Task ReleasesConcurrentBorrows()
    {
        var (lifecycle, manager) = Ready(); var operation = Guid.NewGuid();
        using var root = Acquire(manager, operation);
        using var anchor = root.Borrow(manager, operation, [Mutation]);
        var retained = Enumerable.Range(0, 64).Select(_ => root.Borrow(manager, operation, [Mutation])).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, 64).Select(index => Task.Run(async () =>
        {
            await start.Task;
            retained[index].VerifyHeld();
            retained[index].Dispose(); retained[index].Dispose();
            ResourceOperationBorrow borrow;
            try { borrow = root.Borrow(manager, operation, [Mutation]); }
            catch (ObjectDisposedException) { return; }
            using (borrow)
            {
                borrow.VerifyHeld();
                Assert.Equal(1, borrow.Use(() => 1));
                borrow.Dispose(); borrow.Dispose();
            }
        })).Append(Task.Run(async () => { await start.Task; root.Dispose(); root.Dispose(); })).ToArray();
        start.SetResult();
        await Task.WhenAll(workers);
        anchor.VerifyHeld();
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [Mutation]).Denial);
        anchor.Dispose();
        using (Acquire(manager, Guid.NewGuid())) { }
        Assert.NotNull(lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "Release Waiters Wake Only After The Final Child Borrow Frees The Actual Mutation Claim")]
    public async Task SignalsFinalRelease()
    {
        var (_, manager) = Ready();
        using var root = Acquire(manager, Guid.NewGuid());
        var borrowed = root.Borrow(manager, root.OperationId, [Mutation]);
        var released = root.WhenReleased;
        root.Dispose();
        Assert.False(released.IsCompleted);
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [Mutation]).Denial);
        borrowed.Dispose();
        await released.WaitAsync(TimeSpan.FromSeconds(5));
        using var next = Acquire(manager, Guid.NewGuid());
        Assert.True(root.WhenReleased.IsCompletedSuccessfully);
    }

    [Fact(DisplayName = "Cancelling One Release Wait Does Not Release The Owning Operation Or Its Claims")]
    public async Task CancelsOnlyWaiter()
    {
        var (_, manager) = Ready();
        using var root = Acquire(manager, Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var waiting = root.WhenReleased.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(root.IsActive);
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [Mutation]).Denial);
        root.Dispose();
        await root.WhenReleased.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Conflicting Resource Admission Waits On The Actual Writer Release Without Polling Or Losing Its Wakeup")]
    public async Task WaitsForMutation()
    {
        var (_, manager) = Ready();
        using var writer = Acquire(manager, Guid.NewGuid());
        var waiting = manager.AcquireAsync(Guid.NewGuid(), [new(Mutation.ResourceKey, ResourceClaimMode.Reference)], default).AsTask();
        Assert.False(waiting.IsCompleted);
        writer.Dispose();
        var admission = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = Assert.IsType<ResourceOperationLease>(admission.Lease);
        Assert.Equal(ResourceLeaseDenial.None, admission.Denial);
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [Mutation]).Denial);
    }

    [Fact(DisplayName = "Cancelling Queued Resource Admission Cannot Release Or Borrow The Current Writer")]
    public async Task CancelsQueuedAdmission()
    {
        var (_, manager) = Ready();
        using var writer = Acquire(manager, Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var waiting = manager.AcquireAsync(Guid.NewGuid(), [Mutation], cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(writer.IsActive);
        Assert.Equal(ResourceLeaseDenial.Conflict, manager.TryAcquire(Guid.NewGuid(), [Mutation]).Denial);
        writer.Dispose();
        using var next = Acquire(manager, Guid.NewGuid());
    }

    private static ResourceOperationLease Acquire(ResourceLeaseManager manager, Guid operation, ResourceClaim? claim = null) =>
        Assert.IsType<ResourceOperationLease>(manager.TryAcquire(operation, [claim ?? Mutation]).Lease);
    private static (HostLifecycle, ResourceLeaseManager) Ready()
    {
        var index = new RecoveryIndex(); index.PublishVerified(0, 1, []);
        var lifecycle = new HostLifecycle(index, TimeProvider.System); return (lifecycle, new(lifecycle));
    }
}
