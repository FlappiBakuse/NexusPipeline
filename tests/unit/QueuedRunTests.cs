using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using Xunit;

namespace NexusPipeline.CoreTests;

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
