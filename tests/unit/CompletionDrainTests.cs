using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class CompletionDrainTests
{
    [Fact(DisplayName = "An Idle Reference Drain Waits For Accepted Borrows And Does Not Admit New References")]
    public void WaitsForReferences()
    {
        var lifecycle = new HostLifecycle(Ready(), TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        var operation = Guid.NewGuid();
        ResourceClaim[] claims = [new("extension:fixture", ResourceClaimMode.Reference)];
        using var reference = resources.TryAcquire(operation, claims).Lease!;
        Assert.NotNull(reference);
        Assert.Null(lifecycle.BeginCompletionDrain(Guid.NewGuid(), onlyWhenIdle: true));
        using var borrow = reference.Borrow(resources, operation, claims);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid(), onlyWhenIdle: true, drainReferences: true);
        Assert.NotNull(drain); Assert.False(drain.TryReserve());
        Assert.Null(resources.TryAcquire(Guid.NewGuid(), claims).Lease);
        reference.Dispose(); Assert.False(drain.TryReserve());
        borrow.Dispose(); Assert.True(drain.TryReserve()); Assert.True(drain.TryEnterEffect());
        using var payload = resources.TryCompletePayload(drain).Lease;
        Assert.NotNull(payload);
    }

    [Fact(DisplayName = "A Reference Drain Still Rejects Active Mutations And Can Restore Admission Before Its Effect")]
    public void PreservesMutationProtection()
    {
        var lifecycle = new HostLifecycle(Ready(), TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        using var mutation = resources.TryAcquire(Guid.NewGuid(), [new("settings", ResourceClaimMode.Mutation)]).Lease!;
        Assert.Null(lifecycle.BeginCompletionDrain(Guid.NewGuid(), onlyWhenIdle: true, drainReferences: true));
        Assert.Equal(HostLifecyclePhase.Running, lifecycle.Phase);
        mutation.Dispose();
        using var reference = resources.TryAcquire(Guid.NewGuid(), [new("extension:fixture", ResourceClaimMode.Reference)]).Lease!;
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid(), onlyWhenIdle: true, drainReferences: true);
        Assert.NotNull(drain); Assert.False(drain.TryReserve()); Assert.True(drain.TryCancel());
        using var next = resources.TryAcquire(Guid.NewGuid(), [new("settings", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(next); Assert.Equal(HostLifecyclePhase.Running, lifecycle.Phase);
    }

    [Theory(DisplayName = "An Idle Reference Drain Never Admits An Accepted Run Or Its Notification Queue")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesAcceptedWork(bool notifications)
    {
        var lifecycle = new HostLifecycle(Ready(), TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle); var operation = Guid.NewGuid();
        using var root = resources.TryAcquire(operation, [new("execution:fixture", ResourceClaimMode.Mutation)]).Lease!;
        using var run = root.AcceptRun(resources, operation, Guid.NewGuid()); root.Dispose();
        using var queue = notifications ? resources.RegisterNotificationQueue(run, Guid.NewGuid()) : null;
        if (notifications) run.Dispose();
        Assert.Null(lifecycle.BeginCompletionDrain(Guid.NewGuid(), onlyWhenIdle: true, drainReferences: true));
        Assert.Equal(HostLifecyclePhase.Running, lifecycle.Phase);
        queue?.Dispose(); run.Dispose();
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid(), onlyWhenIdle: true, drainReferences: true);
        Assert.NotNull(drain); Assert.True(drain.TryReserve());
    }

    [Fact(DisplayName = "Completion Draining Freezes New Admission And Only Its Own Cancel Restores It After Existing Work Finishes")]
    public void DrainsNewAdmission()
    {
        var recovery = Ready();
        var lifecycle = new HostLifecycle(recovery, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        using var work = resources.TryAcquire(Guid.NewGuid(), [new("work", ResourceClaimMode.Mutation)]).Lease!;
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid());
        Assert.NotNull(drain);
        Assert.False(drain.TryReserve());
        Assert.Null(resources.TryAcquire(Guid.NewGuid(), [new("other", ResourceClaimMode.Mutation)]).Lease);
        Assert.Equal("completion_action_pending", lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).ReasonCode);
        work.Dispose();
        Assert.True(drain.TryReserve());
        Assert.False(lifecycle.CanFinalizeAfterExit(Guid.NewGuid()));
        Assert.True(drain.TryCancel());
        Assert.False(drain.TryEnterEffect());
        using var next = resources.TryAcquire(Guid.NewGuid(), [new("other", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(next);
    }

    [Theory(DisplayName = "A Changed Recovery Projection Blocks The Actual Effect Even After The Countdown Was Reserved")]
    [InlineData(false)]
    [InlineData(true)]
    public void RechecksRecoveryProjection(bool isolation)
    {
        var recovery = Ready();
        var lifecycle = new HostLifecycle(recovery, TimeProvider.System);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid());
        Assert.NotNull(drain); Assert.True(drain.TryReserve());
        if (isolation) recovery.PublishVerified(1, 2, [new("config", Guid.NewGuid(), "writer_unknown")]);
        else recovery.BlockUnknown("recovery_unverified");
        Assert.False(drain.TryEnterEffect());
        Assert.True(drain.TryCancel());
        Assert.Equal(HostLifecyclePhase.Running, lifecycle.Phase);
    }

    [Fact(DisplayName = "Cancellation Cannot Reopen Admission After The Native Effect Has Been Entered Without A Rejected Result")]
    public void KeepsEnteredDrain()
    {
        var lifecycle = new HostLifecycle(Ready(), TimeProvider.System);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid());
        Assert.NotNull(drain); Assert.True(drain.TryReserve()); Assert.True(drain.TryEnterEffect());
        Assert.False(drain.TryCancel());
        Assert.False(drain.TryEnterEffect());
        Assert.Equal(HostLifecyclePhase.ClosePermitted, lifecycle.Phase);
        Assert.True(drain.ReleaseFinishedEffect());
        Assert.Equal(HostLifecyclePhase.Running, lifecycle.Phase);
    }

    [Fact(DisplayName = "A Recovery Hold Published By Already Accepted Work Can Finish Only Its Original Claims During Completion Drain")]
    public void PermitsOwnedContinuation()
    {
        var recovery = Ready();
        var lifecycle = new HostLifecycle(recovery, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        var operation = Guid.NewGuid();
        using var original = resources.TryAcquire(operation, [new("config", ResourceClaimMode.Mutation)]).Lease!;
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid()); Assert.NotNull(drain);
        recovery.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations,
            [new(operation, "config", "configuration_restore_pending"), new(operation, "outside", "configuration_restore_pending")]);
        original.Dispose();
        var unauthorized = recovery.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, operation, new HashSet<string> { "outside" });
        Assert.NotNull(unauthorized);
        Assert.Null(resources.TryContinue(unauthorized, [new("outside", ResourceClaimMode.Mutation)]).Lease);
        var continuation = recovery.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, operation, new HashSet<string> { "config" });
        Assert.NotNull(continuation);
        using var finishing = resources.TryContinue(continuation, [new("config", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(finishing); Assert.False(drain.TryReserve());
        finishing.Dispose();
        recovery.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
        Assert.True(drain.TryReserve());
    }

    [Fact(DisplayName = "A Reference Accepted Before Draining Cannot Be Promoted To Mutation By A Later Recovery Hold")]
    public void RejectsReferencePromotion()
    {
        var recovery = Ready();
        var lifecycle = new HostLifecycle(recovery, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        var operation = Guid.NewGuid();
        using var original = resources.TryAcquire(operation, [new("package", ResourceClaimMode.Reference)]).Lease!;
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid()); Assert.NotNull(drain);
        recovery.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, [new(operation, "package", "restore_pending")]);
        original.Dispose();
        var continuation = recovery.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, operation, new HashSet<string> { "package" });
        Assert.NotNull(continuation);
        Assert.Null(resources.TryContinue(continuation, [new("package", ResourceClaimMode.Mutation)]).Lease);
        Assert.False(drain.TryReserve());
    }

    private static RecoveryIndex Ready()
    {
        var recovery = new RecoveryIndex(); recovery.PublishVerified(0, 1, []); return recovery;
    }
}
