using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScheduleWaitingTests
{
    [Theory]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("disable")]
    [InlineData("reschedule")]
    public async Task RevalidatesWaitingSources(string edit)
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        var occurrence = fixture.CommitOnly();
        var catalog = fixture.Source.Definitions.Read(); var queue = Assert.Single(catalog.Queues);
        var changes = edit == "delete"
            ? new AutomationChanges(catalog.Revision, [], [], [], [], [], [],
                [new(AutomationDefinitionKind.Schedule, fixture.ScheduleId, catalog.Schedules.Single().Revision), new(AutomationDefinitionKind.Queue, queue.Id, queue.Revision)])
            : new AutomationChanges(catalog.Revision, [], [], [], [], [queue with { Name = "Changed after occurrence" }], [], []);
        if (edit is "disable" or "reschedule") changes = new(catalog.Revision, [], [], [], [], [],
            [catalog.Schedules.Single() with { IsEnabled = edit != "disable", LocalMinute = edit == "reschedule"
                ? (catalog.Schedules.Single().LocalMinute + 1) % 1440 : catalog.Schedules.Single().LocalMinute }], []);
        fixture.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "schedule-change", changes), changes, DateTimeOffset.UtcNow);
        await fixture.Scheduler.TickAsync();
        Assert.Empty(fixture.Store.ReadWaiting(64));
        if (edit == "rename")
        {
            Assert.Equal(1, fixture.Base.Inbox.PendingCount); Assert.Equal(0, fixture.Count("schedule_occurrence_outcomes"));
            Assert.Equal(1, fixture.Count("operation_journals")); Assert.Equal(0, fixture.Count("journal_writer_job_roots"));
            ScheduleMinuteInventory.Verify(fixture.Source.Database); return;
        }
        Assert.Equal(0, fixture.Base.Inbox.PendingCount);
        Assert.Equal(1, fixture.Count("schedule_occurrence_outcomes"));
        Assert.Equal(0, fixture.Count("journal_writer_job_roots"));
        Assert.Equal(0, fixture.Count("operation_journals"));
        ScheduleMinuteInventory.Verify(fixture.Source.Database);
        var origin = Assert.Throws<IOException>(() => PreparedExecutionOrigin.Capture(
            fixture.Source.Instance, fixture.Source.Database, fixture.Source.Lease, occurrence.Frozen.OccurrenceId));
        Assert.Equal("execution_occurrence_blocked", origin.Message);
        await fixture.Scheduler.TickAsync();
        Assert.Equal(1, fixture.Count("schedule_occurrence_outcomes"));
    }

    [Fact(DisplayName = "A Cold Owner Blocks The Unaccepted Waiting Occurrence Without Recreating Its Measured Counter")]
    public async Task BlocksColdOccurrences()
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        var occurrence = fixture.CommitOnly();
        using (var preview = fixture.Base.Service.DryRun(fixture.Source.Request(new QueueTarget(fixture.Source.QueueId)), DateTimeOffset.UtcNow))
        {
            var key = ExecutionPlanResources.Claims((preview.ReadPlan() ?? throw new InvalidOperationException()).ReadWire())
                .First(claim => claim.Mode == ResourceClaimMode.Mutation && claim.ResourceKey.StartsWith("automation:", StringComparison.Ordinal));
            var blocked = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [key]);
            using var blocker = Assert.IsType<ResourceOperationLease>(blocked.Lease);
            await fixture.Scheduler.TickAsync();
        }
        var saved = Assert.IsType<RunBudgetObservation>(fixture.Budget(occurrence.Frozen.RunId));
        Assert.Null(saved.JournalId); Assert.Equal(0, fixture.Base.Inbox.PendingCount);
        fixture.Source.Lease.Dispose();
        using var lease = InstanceLease.Acquire(fixture.Source.Instance, new());
        var index = new StartupJournalRecovery(fixture.Source.Instance, new(), fixture.Source.Database,
            new(Path.Combine(fixture.Source.Root, "host"), Path.Combine(fixture.Source.Instance.RuntimeRoot, "extensions"))).Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var inbox = new ExecutionAdmissionInbox();
        var service = new ExecutionAdmissionService(fixture.Source.Instance, fixture.Source.Database, new(), lease,
            resources, index, inbox, new(5, 6, 7));
        var store = new ScheduleOccurrenceStore(fixture.Source.Instance, fixture.Source.Database, lease, resources, fixture.Clock);
        var scheduler = new ScheduleRuntimeService(store, new ScheduledExecutionAdmission(fixture.Source.Instance,
            fixture.Source.Database, lease, service, fixture.Clock), fixture.Clock);
        await scheduler.TickAsync(); await scheduler.StopAsync();
        Assert.Equal(saved, fixture.Budget(occurrence.Frozen.RunId));
        Assert.Empty(store.ReadWaiting(64)); Assert.Equal(0, inbox.PendingCount);
        Assert.Equal(1, fixture.Count("schedule_occurrence_outcomes"));
        Assert.Equal(0, fixture.Count("journal_writer_job_roots"));
        var rejected = Assert.Throws<IOException>(() => service.StartScheduled(
            PreparedExecutionOrigin.Capture(fixture.Source.Instance, fixture.Source.Database, lease, occurrence.Frozen.OccurrenceId),
            DateTimeOffset.UtcNow));
        Assert.Equal("execution_occurrence_blocked", rejected.Message);
    }

    [Fact(DisplayName = "Store Owned Current Minute Sampling Does Not Commit A Stale Caller Scan Across A Minute Boundary")]
    public void RechecksCurrentMinute()
    {
        var clock = new MovingScheduleClock(DateTimeOffset.UtcNow);
        using var fixture = new ScheduleNativeFixture(clock);
        var checkpoint = fixture.Store.ReadCheckpoint();
        var scan = ScheduleMinutePolicy.Scan(fixture.Source.Instance.InstanceId, fixture.Source.Definitions.Read(), checkpoint.Watermark, clock.GetUtcNow());
        clock.Advance();
        Assert.Equal("schedule_scan_changed", Assert.Throws<IOException>(() => fixture.Store.CommitMinute(scan)).Message);
        var calls = clock.ReadCount;
        _ = fixture.Store.CommitCurrentMinute();
        Assert.Equal(calls + 1, clock.ReadCount);
        Assert.Equal(0, fixture.Store.ReadCheckpoint().Watermark.Revision);
        Assert.Equal(0, fixture.Count("journal_writer_job_roots"));
        ScheduleMinuteInventory.Verify(fixture.Source.Database);
    }

    private sealed class MovingScheduleClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        internal int ReadCount { get; private set; }
        public override DateTimeOffset GetUtcNow() { ReadCount++; return _now; }
        internal void Advance() => _now = _now.AddMinutes(1);
    }
}
