using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;

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

public sealed class QueuedRunTests
{
    [Fact]
    public void RefreshesPendingSources()
    {
        var lifecycle = new HostLifecycle(Ready(), TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        var start = new OperationIdentity(Guid.NewGuid(), "host:scheduler", new string('a', 64), "execution.start");
        var request = new ExecutionSelectionRequest(new QueueTarget(Guid.NewGuid()), 1, 0);
        using var metadata = resources.TryAcquire(Guid.NewGuid(), [new("scheduling:state", ResourceClaimMode.Mutation)]).Lease!;
        using var run = resources.RegisterQueuedRun(metadata, Guid.NewGuid(), start, request);
        var refreshed = request with { ExpectedCatalogRevision = 2, ExpectedSettingsRevision = 1 };
        var identity = new OperationIdentity(start.OperationId, start.SubjectId, new string('b', 64), start.Kind);
        resources.RefreshQueuedRun(metadata, run, identity, refreshed);
        var found = resources.FindQueuedRun(run.RunId, identity, refreshed);
        Assert.NotNull(found); Assert.Equal(run.RunId, found.RunId); Assert.True(found.IsActive); Assert.True(run.IsActive);
        Assert.Throws<IOException>(() => resources.FindQueuedRun(run.RunId, start, request));
        Assert.Throws<IOException>(() => resources.RefreshQueuedRun(metadata, run, identity, refreshed with { Target = new QueueTarget(Guid.NewGuid()) }));
        Assert.Throws<IOException>(() => resources.RefreshQueuedRun(metadata, run,
            new(identity.OperationId, "foreign", identity.RequestDigest, identity.Kind), refreshed));
        Assert.Throws<IOException>(() => resources.RefreshQueuedRun(metadata, run, start, request));
        using var first = resources.TryContinueRun(run, start.OperationId, [new("script", ResourceClaimMode.Mutation)]).Lease!;
        var accepted = first.AcceptRun(resources, start.OperationId, run.RunId);
        Assert.Equal(run.RunId, accepted.RunId); Assert.True(accepted.IsActive);
        Assert.Throws<IOException>(() => resources.RefreshQueuedRun(metadata, run, identity, refreshed));
    }

    [Fact(DisplayName = "An Already Accepted Metadata Writer Can Finish Its Registration During Drain And Unknown Ownership Blocks Close Before It Releases")]
    public void DrainsAcceptedRegistration()
    {
        var index = Ready(); var lifecycle = new HostLifecycle(index, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        var start = new OperationIdentity(Guid.NewGuid(), "host:scheduler", new string('a', 64), "execution.start");
        var request = new ExecutionSelectionRequest(new QueueTarget(Guid.NewGuid()), 1, 0);
        using var metadata = resources.TryAcquire(Guid.NewGuid(), [new("scheduling:state", ResourceClaimMode.Mutation)]).Lease!;
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid()); Assert.NotNull(drain);
        using var run = resources.RegisterQueuedRun(metadata, Guid.NewGuid(), start, request);
        Assert.False(drain.TryReserve());
        resources.ReportUncertainOwnership(metadata, "fixture_metadata_unknown"); metadata.Dispose(); run.Dispose();
        Assert.False(index.Current.IsReady); Assert.False(drain.TryReserve());
        Assert.Equal("fixture_metadata_unknown", index.Current.GlobalBlockReason);
    }

    [Fact(DisplayName = "A Queued Source Has No Physical Pins And Promotes The Same Logical Permit Before Its First Root Releases")]
    public void PromotesLogicalPermit()
    {
        var index = Ready(); var lifecycle = new HostLifecycle(index, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        var start = new OperationIdentity(Guid.NewGuid(), "host:scheduler", new string('a', 64), "execution.start");
        var request = new ExecutionSelectionRequest(new QueueTarget(Guid.NewGuid()), 1, 0); var runId = Guid.NewGuid();
        using var metadata = resources.TryAcquire(Guid.NewGuid(), [new("scheduling:state", ResourceClaimMode.Mutation)]).Lease!;
        using var run = resources.RegisterQueuedRun(metadata, runId, start, request);
        var repeated = resources.RegisterQueuedRun(metadata, runId, start, request);
        Assert.Equal(runId, repeated.RunId); Assert.True(repeated.IsActive); metadata.Dispose();
        using (var editable = resources.TryAcquire(Guid.NewGuid(), [new("script", ResourceClaimMode.Mutation)]).Lease) Assert.NotNull(editable);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid()); Assert.NotNull(drain); Assert.False(drain.TryReserve());
        Assert.Null(resources.TryAcquire(Guid.NewGuid(), [new("script", ResourceClaimMode.Mutation)]).Lease);
        Assert.Null(resources.TryContinueRun(run, Guid.NewGuid(), [new("script", ResourceClaimMode.Mutation)]).Lease);
        using var first = resources.TryContinueRun(run, start.OperationId,
            [new("script", ResourceClaimMode.Mutation), new("package", ResourceClaimMode.Reference)]).Lease;
        Assert.NotNull(first); var accepted = first.AcceptRun(resources, start.OperationId, runId);
        Assert.Equal(runId, accepted.RunId); Assert.True(accepted.IsActive); first.Dispose();
        Assert.False(drain.TryReserve());
        Assert.Null(resources.TryContinueRun(run, Guid.NewGuid(), [new("outside", ResourceClaimMode.Mutation)]).Lease);
        Assert.Null(resources.TryContinueRun(run, Guid.NewGuid(), [new("package", ResourceClaimMode.Mutation)]).Lease);
        using (var retry = resources.TryContinueRun(run, Guid.NewGuid(), [new("script", ResourceClaimMode.Mutation)]).Lease) Assert.NotNull(retry);
        run.Dispose(); Assert.False(repeated.IsActive); Assert.False(accepted.IsActive); Assert.True(drain.TryReserve());
    }

    [Fact(DisplayName = "A Queued Permit Requires The Actual Scheduling Mutation Root And Cannot Cross Coordinators")]
    public void RequiresSchedulingOwner()
    {
        var index = Ready(); var lifecycle = new HostLifecycle(index, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        var foreign = new ResourceLeaseManager(lifecycle);
        var start = new OperationIdentity(Guid.NewGuid(), "host:scheduler", new string('a', 64), "execution.start");
        var request = new ExecutionSelectionRequest(new QueueTarget(Guid.NewGuid()), 1, 0);
        using var readOnly = resources.TryAcquire(Guid.NewGuid(), [new("scheduling:state", ResourceClaimMode.Reference)]).Lease!;
        Assert.Throws<InvalidOperationException>(() => resources.RegisterQueuedRun(readOnly, Guid.NewGuid(), start, request)); readOnly.Dispose();
        using var metadata = resources.TryAcquire(Guid.NewGuid(), [new("scheduling:state", ResourceClaimMode.Mutation)]).Lease!;
        Assert.Throws<InvalidOperationException>(() => foreign.RegisterQueuedRun(metadata, Guid.NewGuid(), start, request));
        var runId = Guid.NewGuid(); using var run = resources.RegisterQueuedRun(metadata, runId, start, request);
        Assert.Throws<IOException>(() => resources.FindQueuedRun(runId,
            new OperationIdentity(start.OperationId, "another", start.RequestDigest, start.Kind), request));
        Assert.Null(foreign.TryContinueRun(run, start.OperationId, [new("script", ResourceClaimMode.Mutation)]).Lease);
    }

    [Theory(DisplayName = "A Queued Continuation Rejects A Changed Operation Subject Or Frozen Request Before Calling The Port")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RejectsChangedRequests(int changed)
    {
        var index = Ready(); var lifecycle = new HostLifecycle(index, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        var start = new OperationIdentity(Guid.NewGuid(), "host:scheduler", new string('a', 64), "execution.start");
        var request = new ExecutionSelectionRequest(new QueueTarget(Guid.NewGuid()), 1, 0);
        using var metadata = resources.TryAcquire(Guid.NewGuid(), [new("scheduling:state", ResourceClaimMode.Mutation)]).Lease!;
        using var run = resources.RegisterQueuedRun(metadata, Guid.NewGuid(), start, request);
        var port = new UncalledPort();
        Assert.Throws<IOException>(() => new AdmissionCoordinator(resources, port).ContinueAcceptedRun(run,
            changed == 0 ? Guid.NewGuid() : start.OperationId, changed == 1 ? "another" : start.SubjectId,
            changed == 2 ? request with { ExpectedCatalogRevision = 2 } : request, DateTimeOffset.UtcNow));
        Assert.Equal(0, port.Calls);
    }

    private static RecoveryIndex Ready() { var value = new RecoveryIndex(); value.PublishVerified(0, 1, []); return value; }
    private sealed class UncalledPort : IExecutionAdmissionPort
    {
        internal int Calls { get; private set; }
        public OperationIdentity Identity(Guid operationId, string subject, ExecutionSelectionRequest request) { Calls++; throw new InvalidOperationException(); }
        public IExecutionAdmissionReceipt? Lookup(OperationIdentity identity) => throw new InvalidOperationException();
        public IExecutionAdmissionDraft Prepare(ExecutionSelectionRequest request, DateTimeOffset createdAt) => throw new InvalidOperationException();
        public IPreparedExecutionAdmission Freeze(OperationIdentity identity, IExecutionAdmissionDraft draft, ResourceOperationLease root) => throw new InvalidOperationException();
        public IExecutionAdmissionReceipt CommitAndHandoff(OperationIdentity identity, IExecutionAdmissionDraft draft, IPreparedExecutionAdmission prepared, ResourceOperationLease root, DateTimeOffset acceptedAt) => throw new InvalidOperationException();
    }
}
