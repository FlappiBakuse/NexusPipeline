using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class AcceptedQueueTests
{
    [Fact(DisplayName = "An Already Accepted Run Can Publish Notification Work During Drain Without Retaining Execution Resources")]
    public void PublishesDuringDrain()
    {
        var index = Ready(); var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        var operation = Guid.NewGuid();
        using var root = resources.TryAcquire(operation, [new("execution:fixture", ResourceClaimMode.Mutation)]).Lease!;
        using var run = root.AcceptRun(resources, operation, Guid.NewGuid()); root.Dispose();
        using (var editable = resources.TryAcquire(Guid.NewGuid(), [new("execution:fixture", ResourceClaimMode.Mutation)]).Lease)
            Assert.NotNull(editable);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid()); Assert.NotNull(drain);
        using var queue = resources.RegisterNotificationQueue(run, Guid.NewGuid());
        run.Dispose(); Assert.False(drain.TryReserve());
        Assert.Null(resources.TryAcquire(Guid.NewGuid(), [new("notifications:catalog", ResourceClaimMode.Reference)]).Lease);
        using var sender = resources.TryContinueQueue(queue, Guid.NewGuid()).Lease;
        Assert.NotNull(sender);
        foreach (var resource in new[] { "notifications:catalog", "secrets:fields", "settings:host", "blobs:content" })
            Assert.Contains(sender.Claims, claim => claim.ResourceKey == resource);
        Assert.All(sender.Claims, claim => Assert.Equal(ResourceClaimMode.Reference, claim.Mode));
        queue.Dispose(); Assert.False(drain.TryReserve());
        sender.Dispose(); Assert.True(drain.TryReserve());
        Assert.Null(resources.TryContinueQueue(queue, Guid.NewGuid()).Lease);
    }

    [Fact(DisplayName = "A Startup Queue Requires Its Real Metadata Root And Does Not Pin Catalog Edits Between Sends")]
    public void PinsStartupMetadata()
    {
        var index = Ready(); var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle); var foreign = new ResourceLeaseManager(lifecycle);
        using var unrelated = resources.TryAcquire(Guid.NewGuid(), [new("other", ResourceClaimMode.Mutation)]).Lease!;
        Assert.Throws<InvalidOperationException>(() => resources.RegisterNotificationQueue(unrelated));
        var operation = Guid.NewGuid();
        using var root = resources.TryAcquire(operation, [new("notifications:queue", ResourceClaimMode.Mutation)]).Lease!;
        Assert.Throws<InvalidOperationException>(() => foreign.RegisterNotificationQueue(root));
        using var queue = resources.RegisterNotificationQueue(root); root.Dispose();
        using var edit = resources.TryAcquire(Guid.NewGuid(), [new("notifications:catalog", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(edit);
        Assert.Equal(ResourceLeaseDenial.Conflict, resources.TryContinueQueue(queue, Guid.NewGuid()).Denial);
        Assert.Null(foreign.TryContinueQueue(queue, Guid.NewGuid()).Lease);
        edit.Dispose();
        using var sender = resources.TryContinueQueue(queue, Guid.NewGuid()).Lease;
        Assert.NotNull(sender);
    }

    [Fact(DisplayName = "Notification Continuation Cannot Override Recovery Or Reactivate A Released Run Or Queue")]
    public void RejectsReleasedContinuations()
    {
        var index = Ready(); var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        var operation = Guid.NewGuid();
        using var root = resources.TryAcquire(operation, [new("execution:fixture", ResourceClaimMode.Mutation)]).Lease!;
        using var run = root.AcceptRun(resources, operation, Guid.NewGuid()); root.Dispose();
        using var queue = resources.RegisterNotificationQueue(run, Guid.NewGuid()); run.Dispose();
        Assert.Throws<InvalidOperationException>(() => resources.RegisterNotificationQueue(run, Guid.NewGuid()));
        index.PublishVerified(1, 2, [new("secrets:fields", Guid.NewGuid(), "unknown_cipher_writer")]);
        Assert.Null(resources.TryContinueQueue(queue, Guid.NewGuid()).Lease);
        index.PublishVerified(2, 3, []);
        using var sender = resources.TryContinueQueue(queue, Guid.NewGuid()).Lease;
        Assert.NotNull(sender); sender.Dispose();
        index.BlockUnknown("metadata_unknown");
        Assert.Null(resources.TryContinueQueue(queue, Guid.NewGuid()).Lease);
        queue.Dispose(); Assert.False(queue.IsActive);
    }

    private static RecoveryIndex Ready()
    { var index = new RecoveryIndex(); index.PublishVerified(0, 1, []); return index; }
}
