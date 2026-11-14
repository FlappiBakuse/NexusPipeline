using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Scheduling;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class QueueRuntimeTests
{
    [Fact(DisplayName = "An Actual Startup Queue Keeps Its Identity Through Resource Conflict And Starts Only Once")]
    public async Task KeepsStartupIdentity()
    {
        using var fixture = new ScheduleNativeFixture(); Enable(fixture);
        var store = new StartupQueueStore(fixture.Source.Instance, fixture.Source.Database, fixture.Source.Lease, fixture.Base.Resources, TimeProvider.System);
        var batch = store.Commit(); var occurrence = Assert.Single(batch.Occurrences);
        Assert.Equal(occurrence, Assert.Single(store.Commit().Occurrences));
        using var preview = fixture.Base.Service.DryRun(fixture.Source.Request(new QueueTarget(fixture.Source.QueueId)), DateTimeOffset.UtcNow);
        var claims = ExecutionPlanResources.Claims((preview.ReadPlan() ?? throw new InvalidOperationException()).ReadWire());
        var conflicting = claims.First(claim => claim.Mode == ResourceClaimMode.Mutation && claim.ResourceKey.StartsWith("automation:", StringComparison.Ordinal));
        using var blocker = Assert.IsType<ResourceOperationLease>(fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [conflicting]).Lease);
        var dispatch = new StartupQueueDispatch(fixture.Source.Instance, fixture.Source.Database, fixture.Source.Lease, store, fixture.Base.Service, TimeProvider.System);
        dispatch.Tick(default);
        Assert.Equal(0, fixture.Base.Inbox.PendingCount); Assert.Equal(occurrence, Assert.Single(store.ReadWaiting()).Occurrence);
        var firstBudget = Assert.IsType<RunBudgetObservation>(fixture.Budget(occurrence.RunId));
        blocker.Dispose(); dispatch.Tick(default);
        Assert.Equal(1, fixture.Base.Inbox.PendingCount);
        var acceptedBudget = Assert.IsType<RunBudgetObservation>(fixture.Budget(occurrence.RunId));
        Assert.Equal(firstBudget.FirstPreparedAtUtc, acceptedBudget.FirstPreparedAtUtc); Assert.Equal(firstBudget.DeadlineUtc, acceptedBudget.DeadlineUtc);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(occurrence.RunId, receipt.Facts.RunId); fixture.RequireActualClosure(occurrence.StartOperationId);
        dispatch.Tick(default); Assert.Empty(store.ReadWaiting()); Assert.Null(await fixture.Consumer.RunNextAsync());
        Assert.Equal(1, fixture.Count("startup_queue_batches")); Assert.Equal(1, fixture.Count("journal_writer_job_roots"));
        StartupQueueInventory.Verify(fixture.Source.Instance, fixture.Source.Database);
        RunBudgetInventory.Verify(fixture.Source.Instance, fixture.Source.Database, new());
    }

    [Fact(DisplayName = "Startup Preference Round Trips And Failure To Commit The Batch Starts Nothing")]
    public void RequiresCommittedStartup()
    {
        using var fixture = new ScheduleNativeFixture(); Enable(fixture);
        var catalog = fixture.Source.Definitions.Read(); Assert.True(catalog.Queues.Single().RunOnStartup);
        var codec = new AutomationBundleCodec(new());
        var imported = codec.Decode(codec.Encode(catalog, DateTimeOffset.UtcNow), catalog.Roots.ToDictionary(item => item.Id, item => item.Path), 0);
        Assert.True(imported.Definitions.Queues.Single().RunOnStartup);
        var store = new StartupQueueStore(fixture.Source.Instance, fixture.Source.Database, fixture.Source.Lease, fixture.Base.Resources, TimeProvider.System);
        var before = fixture.Base.Counts();
        fixture.Sql("CREATE TRIGGER main.fail_startup_occurrence BEFORE INSERT ON startup_queue_occurrences BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        try
        {
            Assert.Throws<SqliteException>(() => store.Commit());
            Assert.Equal(before, fixture.Base.Counts()); Assert.Equal(0, fixture.Count("startup_queue_batches"));
            Assert.Equal(0, fixture.Base.Inbox.PendingCount); Assert.Equal(0, fixture.Count("journal_writer_job_roots"));
        }
        finally { fixture.Sql("DROP TRIGGER main.fail_startup_occurrence;"); }
        Assert.Single(store.Commit().Occurrences);
    }

    [Fact(DisplayName = "A New Exclusive Owner Retires Only Unstarted Old Startup Occurrences And Creates A New Generation")]
    public void RetiresUnstartedGenerations()
    {
        using var fixture = new ScheduleNativeFixture(); Enable(fixture);
        var original = new StartupQueueStore(fixture.Source.Instance, fixture.Source.Database, fixture.Source.Lease, fixture.Base.Resources, TimeProvider.System);
        var first = Assert.Single(original.Commit().Occurrences);
        fixture.Source.Lease.Dispose();
        using var nextLease = InstanceLease.Acquire(fixture.Source.Instance, new());
        StartupQueueInventory.Verify(fixture.Source.Instance, fixture.Source.Database);
        var index = new StartupJournalRecovery(fixture.Source.Instance, new(), fixture.Source.Database,
            new(Path.Combine(fixture.Source.Root, "host"), Path.Combine(fixture.Source.Instance.RuntimeRoot, "extensions"))).Recover(nextLease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        var next = new StartupQueueStore(fixture.Source.Instance, fixture.Source.Database, nextLease, resources, TimeProvider.System);
        next.RetirePriorOwners(); var second = Assert.Single(next.Commit().Occurrences);
        Assert.NotEqual(first.OccurrenceId, second.OccurrenceId); Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(second, Assert.Single(next.ReadWaiting()).Occurrence);
        Assert.Equal(1, fixture.Count("startup_queue_outcomes")); Assert.Equal(2, fixture.Count("startup_queue_batches"));
        Assert.Equal(0, fixture.Count("journal_writer_job_roots"));
        StartupQueueInventory.Verify(fixture.Source.Instance, fixture.Source.Database);
    }

    private static void Enable(ScheduleNativeFixture fixture)
    {
        var catalog = fixture.Source.Definitions.Read();
        var change = new AutomationChanges(catalog.Revision, [], [], [], [], [catalog.Queues.Single() with { RunOnStartup = true }], [], []);
        fixture.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "startup-fixture", change), change, DateTimeOffset.UtcNow);
    }
}
