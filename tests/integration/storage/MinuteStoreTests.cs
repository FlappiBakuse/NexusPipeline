using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MinuteStoreTests
{
    [Fact]
    public void IsolatesBadSchedules()
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        var catalog = fixture.Source.Definitions.Read(); var valid = catalog.Schedules.Single();
        var bad = valid with { Id = Guid.NewGuid(), Revision = 0 };
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [], [], [bad], []);
        fixture.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        fixture.Sql($"UPDATE automation.schedules SET time_zone_id='nexus/no-such-zone' WHERE schedule_id='{bad.Id:D}';");
        var occurrence = fixture.CommitOnly();
        Assert.Equal(valid.Id, occurrence.Frozen.ScheduleId);
        Assert.Equal(bad.Id, Assert.Single(fixture.Store.Diagnostics).ScheduleId);
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Fact(DisplayName = "Real Sql Failure Rolls Back The Minute Watermark And Every Occurrence Before Any Start")]
    public void RevertsMinuteFailure()
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        var before = fixture.Base.Counts();
        fixture.Sql("CREATE TRIGGER main.fail_schedule_claim BEFORE INSERT ON schedule_minute_occurrences BEGIN SELECT RAISE(ABORT,'schedule-fixture-fault'); END;");
        Assert.Throws<SqliteException>(() => fixture.CommitOnly());
        Assert.Equal(before, fixture.Base.Counts());
        Assert.Equal(0, fixture.Store.ReadCheckpoint().Watermark.Revision);
        Assert.Empty(fixture.Store.ReadWaiting(64)); Assert.Equal(0, fixture.Count("schedule_minute_claims"));
        fixture.Sql("DROP TRIGGER main.fail_schedule_claim;");
        var saved = fixture.CommitOnly();
        Assert.Equal(1, fixture.Store.ReadCheckpoint().Watermark.Revision);
        Assert.Equal(saved, fixture.CommitOnly()); Assert.Equal(1, fixture.Count("scheduled_occurrences"));
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
    }

    [Fact(DisplayName = "The Ordinary Consumer Runs The Persisted Occurrence Run And Duplicate Ticks Replay It Without A Second Job")]
    public async Task ReplaysPersistedOccurrences()
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        var occurrence = fixture.CommitOnly(); await fixture.Scheduler.TickAsync();
        Assert.Equal(1, fixture.Base.Inbox.PendingCount);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(occurrence.Frozen.RunId, receipt.Facts.RunId);
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        var count = fixture.Base.Count("journal_writer_job_roots");
        Assert.Equal(1, count);
        fixture.RequireActualClosure(occurrence.Frozen.StartOperationId);
        await fixture.Scheduler.TickAsync(); Assert.Null(await fixture.Consumer.RunNextAsync());
        Assert.Equal(count, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Empty(fixture.Store.ReadWaiting(64));
        Assert.Equal(occurrence.Frozen.OccurrenceId, fixture.CommitOnly().Frozen.OccurrenceId);
        ScheduleMinuteInventory.Verify(fixture.Source.Database);
        RunBudgetInventory.Verify(fixture.Source.Instance, fixture.Source.Database, new());
        var checkpoint = Assert.IsType<RunBudgetObservation>(fixture.Budget(receipt.Facts.RunId));
        Assert.Equal(receipt.SourceReceiptDigest, checkpoint.TerminalReceiptDigest);
    }

    [Fact(DisplayName = "A Repeated Local Minute Keeps And Validates Its First Persisted Occurrence")]
    public void KeepsOriginalDstOccurrence()
    {
        var firstTime = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var repeatedTime = firstTime.AddHours(1);
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(firstTime));
        var source = fixture.Source;
        var catalog = source.Definitions.Read();
        var schedule = Assert.Single(catalog.Schedules) with { TimeZoneId = "America/New_York", LocalMinute = 90 };
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [], [], [schedule], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture-dst", changes), changes, firstTime);
        var first = fixture.CommitOnly();
        var repeatedClock = new ScheduleNativeFixture.FrozenScheduleClock(repeatedTime);
        var store = new ScheduleOccurrenceStore(source.Instance, source.Database, source.Lease, fixture.Base.Resources, repeatedClock);
        Assert.Equal(first, Assert.Single(store.CommitCurrentMinute()));
        Assert.Equal(2, fixture.Count("schedule_minute_claims"));
        Assert.Equal(1, fixture.Count("scheduled_occurrences"));
        var originalId = ScheduleMinuteCodec.OperationId(source.Instance.InstanceId, firstTime.ToUnixTimeSeconds() / 60);
        var repeatedId = ScheduleMinuteCodec.OperationId(source.Instance.InstanceId, repeatedTime.ToUnixTimeSeconds() / 60);
        using var reader = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using (var transaction = DatabaseConnections.BeginRead(reader))
        {
            var original = Assert.IsType<ScheduleMinuteDocument>(ScheduleMinuteRows.ReadMinute(reader, transaction, originalId, source.Instance.InstanceId));
            var repeated = Assert.IsType<ScheduleMinuteDocument>(ScheduleMinuteRows.ReadMinute(reader, transaction, repeatedId, source.Instance.InstanceId));
            Assert.Equal(first.Frozen, Assert.Single(original.Scan.Occurrences));
            Assert.Equal(first.Frozen with { TriggerUtcMinute = repeatedTime.ToUnixTimeSeconds() / 60 }, Assert.Single(repeated.Scan.Occurrences));
            Assert.Equal(first, ScheduleMinuteRows.ReadOccurrence(reader, transaction, first.Frozen.OccurrenceId, source.Instance.InstanceId));
            transaction.Commit();
        }

        fixture.Sql($"UPDATE main.operations SET request_digest='{new string('0', 64)}' WHERE operation_id='{originalId:D}';");
        using var damaged = DatabaseConnections.BeginRead(reader);
        Assert.Equal("schedule_minute_metadata_unbound", Assert.Throws<IOException>(() =>
            ScheduleMinuteRows.ReadMinute(reader, damaged, repeatedId, source.Instance.InstanceId)).Message);
    }

    [Fact(DisplayName = "A Mixed DST Replay Returns First And Current Occurrences In Saved Minute Order")]
    public void PreservesMixedDstMinuteOrder()
    {
        var firstTime = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var repeatedTime = firstTime.AddHours(1);
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(firstTime));
        var source = fixture.Source;
        var catalog = source.Definitions.Read();
        var queue = Assert.Single(catalog.Queues);
        var replaced = Assert.Single(catalog.Schedules) with { IsEnabled = false };
        var leftQueue = queue with { Id = Guid.NewGuid(), Revision = 0, Name = "DST left", Order = 1 };
        var middleQueue = queue with { Id = Guid.NewGuid(), Revision = 0, Name = "DST current", Order = 2 };
        var rightQueue = queue with { Id = Guid.NewGuid(), Revision = 0, Name = "DST right", Order = 3 };
        AutomationSchedule At(Guid id, Guid queueId, bool enabled, int order) =>
            new(id, 0, queueId, enabled, "America/New_York", [(int)DayOfWeek.Sunday], 90) { Order = order };
        var left = At(Guid.Parse("00000000-0000-0000-0000-000000000001"), leftQueue.Id, true, replaced.Order + 1);
        var middle = At(Guid.Parse("00000000-0000-0000-0000-000000000002"), middleQueue.Id, false, replaced.Order + 2);
        var right = At(Guid.Parse("00000000-0000-0000-0000-000000000003"), rightQueue.Id, true, replaced.Order + 3);
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [], [leftQueue, middleQueue, rightQueue], [replaced, left, middle, right], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "mixed-dst", changes), changes, firstTime);
        var first = fixture.Store.CommitCurrentMinute();
        Assert.Equal(new[] { left.Id, right.Id }, first.Select(item => item.Frozen.ScheduleId));

        catalog = source.Definitions.Read();
        middle = catalog.Schedules.Single(item => item.Id == middle.Id) with { IsEnabled = true };
        changes = new(catalog.Revision, [], [], [], [], [], [middle], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "mixed-dst", changes), changes, repeatedTime);
        var store = new ScheduleOccurrenceStore(source.Instance, source.Database, source.Lease, fixture.Base.Resources,
            new ScheduleNativeFixture.FrozenScheduleClock(repeatedTime));
        var repeated = store.CommitCurrentMinute();
        Assert.Equal(new[] { left.Id, middle.Id, right.Id }, repeated.Select(item => item.Frozen.ScheduleId));
        Assert.Equal(first[0], repeated[0]);
        Assert.Equal(first[1], repeated[2]);
        Assert.Equal(repeatedTime.ToUnixTimeSeconds() / 60, repeated[1].Frozen.TriggerUtcMinute);

        var blocked = store.BlockWaiting(first[0], "schedule_source_unavailable");
        Assert.Equal(ScheduleOccurrencePhase.Blocked, blocked.Phase);
        Assert.Equal(new[] { blocked, repeated[1], first[1] }, store.CommitCurrentMinute().ToArray());
        Assert.Equal(2, fixture.Count("schedule_minute_claims"));
        Assert.Equal(3, fixture.Count("scheduled_occurrences"));
    }

    [Fact(DisplayName = "Waiting Occurrences From Different Minutes Keep Their Own Settings And Activity Identities")]
    public void RegistersWaitingAcrossMinutes()
    {
        var firstTime = PlanBuilderFixture.CreatedAt;
        var secondTime = firstTime.AddMinutes(1);
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(firstTime));
        var source = fixture.Source;
        var firstSettings = source.Settings.Read();
        var first = fixture.CommitOnly();
        var firstActivity = Activity(first, firstSettings.Revision);

        var catalog = source.Definitions.Read();
        var schedule = Assert.Single(catalog.Schedules) with { LocalMinute = secondTime.Hour * 60 + secondTime.Minute };
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [], [], [schedule], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, secondTime);
        var settings = new NexusPipeline.Core.Settings.HostSettingsChange(firstSettings.Revision,
            firstSettings.Values with { LogLevel = NexusPipeline.Core.Settings.HostLogLevel.Warning });
        source.Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", settings), settings, secondTime);
        var secondSettings = source.Settings.Read().Revision;
        Assert.NotEqual(firstSettings.Revision, secondSettings);
        var clock = new ScheduleNativeFixture.FrozenScheduleClock(secondTime);
        var store = new ScheduleOccurrenceStore(source.Instance, source.Database, source.Lease, fixture.Base.Resources, clock);
        var second = Assert.Single(store.CommitCurrentMinute());
        Assert.NotEqual(first.Frozen.RunId, second.Frozen.RunId);
        Assert.NotEqual(first.Frozen.CatalogRevision, second.Frozen.CatalogRevision);
        var secondActivity = Activity(second, secondSettings);
        Assert.NotSame(firstActivity, secondActivity);
        Assert.Equal(second, Assert.Single(store.CommitCurrentMinute()));
        Assert.Same(firstActivity, Activity(first, firstSettings.Revision));
        Assert.Same(secondActivity, Activity(second, secondSettings));
        Assert.Equal(2, store.ReadWaiting(64).Length);
        Assert.Equal(0, fixture.Count("journal_writer_job_roots"));

        AcceptedExecutionRun Activity(ScheduledOccurrence occurrence, long settingsRevision)
        {
            var request = new ExecutionSelectionRequest(new QueueTarget(occurrence.Frozen.QueueId), occurrence.Frozen.CatalogRevision, settingsRevision);
            var operation = ExecutionStartIdentity.Create(occurrence.Frozen.StartOperationId, occurrence.SubjectId, request);
            return Assert.IsType<AcceptedExecutionRun>(fixture.Base.Resources.FindQueuedRun(occurrence.Frozen.RunId, operation, request));
        }
    }

    [Fact(DisplayName = "A Pending Schedule Snapshot Cannot Register After The Transaction Ends")]
    public void RejectsExpiredPendingSnapshot()
    {
        var now = PlanBuilderFixture.CreatedAt;
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(now));
        var occurrence = fixture.CommitOnly();
        var source = fixture.Source;
        var id = occurrence.Frozen.OccurrenceId;
        var operation = ScheduleMinuteCodec.OperationId(source.Instance.InstanceId, now.ToUnixTimeSeconds() / 60);
        using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        var pending = ScheduleMinuteRows.PendingMinute.Read(connection, transaction, operation, source.Instance.InstanceId, new HashSet<Guid> { id });
        Assert.Equal(occurrence, pending.RequireWaiting(source.Database, id));

        using (var writer = DatabaseConnections.Open(source.Database.DatabasePath, domains: DatabaseDomain.Runtime))
        using (var write = writer.BeginTransaction())
        {
            using var update = DatabaseConnections.Command(writer, write,
                "UPDATE main.schedule_minute_claims SET document_json=json_set(document_json,'$.settingsRevision',100) WHERE operation_id=$0;",
                operation.ToString("D"));
            Assert.Equal(1, update.ExecuteNonQuery());
            writer.DefaultTimeout = 1;
            Assert.Equal(5, Assert.Throws<SqliteException>(write.Commit).SqliteErrorCode);
            Assert.Equal(occurrence, pending.RequireWaiting(source.Database, id));
            transaction.Commit();
            write.Commit();
        }

        using var later = DatabaseConnections.BeginRead(connection);
        Assert.Equal("execution_scheduled_origin_unbound",
            Assert.Throws<IOException>(() => pending.RequireWaiting(source.Database, id)).Message);
        Assert.Equal("schedule_minute_metadata_unbound", Assert.Throws<IOException>(() =>
            ScheduleMinuteRows.PendingMinute.Read(connection, later, operation, source.Instance.InstanceId, new HashSet<Guid> { id })).Message);
    }

    [Theory(DisplayName = "Minute And Point Reads Reject A Broken Start Or Outcome Operation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsBrokenOccurrenceOperation(bool started)
    {
        var now = PlanBuilderFixture.CreatedAt;
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(now));
        var saved = fixture.CommitOnly();
        if (started) await fixture.Scheduler.TickAsync();
        else fixture.Store.BlockWaiting(saved, "schedule_source_unavailable");
        var source = fixture.Source;
        var minute = ScheduleMinuteCodec.OperationId(source.Instance.InstanceId, now.ToUnixTimeSeconds() / 60);
        using var reader = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using (var transaction = DatabaseConnections.BeginRead(reader))
        {
            Assert.NotNull(ScheduleMinuteRows.ReadMinute(reader, transaction, minute, source.Instance.InstanceId));
            Assert.Equal(started ? ScheduleOccurrencePhase.Accepted : ScheduleOccurrencePhase.Blocked,
                ScheduleMinuteRows.ReadOccurrence(reader, transaction, saved.Frozen.OccurrenceId, source.Instance.InstanceId).Phase);
            transaction.Commit();
        }

        fixture.Sql(started
            ? $"UPDATE main.operations SET subject_id='corrupt-fixture' WHERE operation_id='{saved.Frozen.StartOperationId:D}';"
            : $"UPDATE main.operations SET request_digest='{new string('0', 64)}' WHERE operation_id IN " +
                $"(SELECT operation_id FROM main.schedule_occurrence_outcomes WHERE occurrence_id='{saved.Frozen.OccurrenceId:D}');");
        using var damaged = DatabaseConnections.BeginRead(reader);
        Assert.Equal("schedule_minute_metadata_unbound", Assert.Throws<IOException>(() =>
            ScheduleMinuteRows.ReadMinute(reader, damaged, minute, source.Instance.InstanceId)).Message);
        Assert.Equal("schedule_minute_metadata_unbound", Assert.Throws<IOException>(() =>
            ScheduleMinuteRows.ReadOccurrence(reader, damaged, saved.Frozen.OccurrenceId, source.Instance.InstanceId)).Message);
    }

    [Fact(DisplayName = "An Extra Minute Member With No Occurrence Is Rejected By The Minute Read")]
    public void RejectsOrphanMinuteMember()
    {
        var now = PlanBuilderFixture.CreatedAt;
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(now));
        _ = fixture.CommitOnly();
        var source = fixture.Source;
        var minute = ScheduleMinuteCodec.OperationId(source.Instance.InstanceId, now.ToUnixTimeSeconds() / 60);
        fixture.Sql("PRAGMA foreign_keys=OFF; INSERT INTO main.schedule_minute_occurrences(operation_id,position,occurrence_id) " +
            $"VALUES('{minute:D}',1,'{Guid.NewGuid():D}');");
        using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        Assert.Equal("schedule_minute_metadata_unbound", Assert.Throws<IOException>(() =>
            ScheduleMinuteRows.ReadMinute(connection, transaction, minute, source.Instance.InstanceId)).Message);
    }

    [Fact(DisplayName = "An Ordinary Start Blocked Before Acceptance Creates No Budget Or Occurrence Metadata")]
    public void RejectsBlockedStarts()
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        using var preview = fixture.Base.Service.DryRun(fixture.Source.Request(new QueueTarget(fixture.Source.QueueId)), DateTimeOffset.UtcNow);
        var claims = ExecutionPlanResources.Claims((preview.ReadPlan() ?? throw new InvalidOperationException()).ReadWire());
        var key = claims.First(claim => claim.ResourceKey.StartsWith("automation:", StringComparison.Ordinal) &&
            claim.Mode == ResourceClaimMode.Mutation);
        var denied = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [key]);
        using var blocker = Assert.IsType<ResourceOperationLease>(denied.Lease);
        var before = fixture.Base.Counts();
        var error = Assert.Throws<ExecutionSelectionException>(() => fixture.Base.Service.Start(Guid.NewGuid(),
            "ordinary-wait-fixture", fixture.Source.Request(new QueueTarget(fixture.Source.QueueId)), DateTimeOffset.UtcNow));
        Assert.Equal("execution_resource_conflict", error.Message);
        Assert.Equal(before, fixture.Base.Counts());
        Assert.Equal(0, fixture.Count("execution_run_budget_states"));
        Assert.Equal(0, fixture.Count("scheduled_occurrences"));
    }

    [Fact(DisplayName = "Resource Waiting Reuses The Original Measured Budget And Deadline Through Actual Admission And Collection")]
    public async Task KeepsWaitingBudget()
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        var occurrence = fixture.CommitOnly();
        using var preview = fixture.Base.Service.DryRun(fixture.Source.Request(new QueueTarget(fixture.Source.QueueId)), DateTimeOffset.UtcNow);
        var claims = ExecutionPlanResources.Claims((preview.ReadPlan() ?? throw new InvalidOperationException()).ReadWire());
        var blocking = claims.First(claim => claim.Mode == ResourceClaimMode.Mutation &&
            claim.ResourceKey.StartsWith("automation:", StringComparison.Ordinal) && claim.ResourceKey != "automation:definitions");
        var held = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [blocking with { Mode = ResourceClaimMode.Mutation }]);
        using var resource = Assert.IsType<ResourceOperationLease>(held.Lease);
        await fixture.Scheduler.TickAsync();
        Assert.Equal(0, fixture.Base.Inbox.PendingCount);
        var first = Assert.IsType<RunBudgetObservation>(fixture.Budget(occurrence.Frozen.RunId));
        Assert.Null(first.JournalId); Assert.Equal(0, first.Ordinal);
        var catalog = fixture.Source.Definitions.Read();
        var unrelated = new AutomationChanges(catalog.Revision, [], [],
            [catalog.Users.Single(user => user.Id == fixture.Source.UserC) with { Name = "Unrelated edit while waiting" }], [], [], [], []);
        fixture.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", unrelated), unrelated, DateTimeOffset.UtcNow);
        var settings = fixture.Source.Settings.Read();
        var setting = new NexusPipeline.Core.Settings.HostSettingsChange(settings.Revision, settings.Values with { LogLevel = NexusPipeline.Core.Settings.HostLogLevel.Warning });
        fixture.Source.Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", setting), setting, DateTimeOffset.UtcNow);
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        resource.Dispose();
        await fixture.Scheduler.TickAsync();
        Assert.Equal(1, fixture.Base.Inbox.PendingCount);
        var accepted = Assert.IsType<RunBudgetObservation>(fixture.Budget(occurrence.Frozen.RunId));
        Assert.Equal(first.FirstPreparedAtUtc, accepted.FirstPreparedAtUtc);
        Assert.Equal(first.DeadlineUtc, accepted.DeadlineUtc);
        Assert.True(accepted.ConsumedMilliseconds >= first.ConsumedMilliseconds + 100);
        Assert.NotNull(accepted.JournalId);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(occurrence.Frozen.RunId, receipt.Facts.RunId);
        fixture.RequireActualClosure(occurrence.Frozen.StartOperationId);
    }

    [Fact(DisplayName = "The Actual Terminal Receipt And Measured Budget Commit Together And A Sql Rollback Never Grants Retry")]
    public async Task CommitsMeasuredBudget()
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        var occurrence = fixture.CommitOnly(); await fixture.Scheduler.TickAsync();
        var owned = fixture.Base.Take(occurrence.Frozen.StartOperationId);
        using var session = new AttemptRuntimeSession(fixture.Source.Instance, fixture.Source.Database, new(),
            fixture.Source.Lease, fixture.Base.Resources, fixture.Base.Index, owned);
        session.PrepareConfigurationGroups();
        var step = Assert.Single(session.StepIds);
        session.PrepareNextStep(step); session.EnterEffect(step);
        Assert.Null(await session.ExecuteStepAsync(step, CancellationToken.None));
        await session.CollectStepAsync(step); await session.RestoreStepAsync(step);
        session.FreezeDispatch(ExecutionStopReason.Completed);
        await session.CollectRemainingAsync(); session.CloseUndispatched(); session.MarkApplied();
        await session.RestoreRemainingAsync(ExecutionStopReason.Completed);
        var before = Assert.IsType<RunBudgetObservation>(fixture.Budget(occurrence.Frozen.RunId));
        fixture.Sql("CREATE TRIGGER records.fail_budget_receipt BEFORE INSERT ON attempt_receipts BEGIN SELECT RAISE(ABORT,'budget-receipt-fault'); END;");
        AttemptReceipt physical;
        try
        {
            Assert.Throws<SqliteException>(() => session.Commit(ExecutionStopReason.Completed));
            Assert.Equal(0, fixture.Base.Count("attempt_receipts"));
            Assert.Equal(before, fixture.Budget(occurrence.Frozen.RunId));
            var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), owned.Receipt.JournalId);
            var result = Assert.IsType<AttemptResultIntent>(AttemptResultCodec.Read(state.Latest.Root, state.Attempt));
            physical = AttemptResultCodec.Receipt(state.Attempt, state.Latest, result.Facts);
            var rejected = Assert.Throws<IOException>(() => fixture.Source.Lease.RunBudgets.ReadForRetry(fixture.Source.Database, physical, TimeProvider.System));
            Assert.Equal("execution_run_budget_unbound", rejected.Message);
        }
        finally { fixture.Sql("DROP TRIGGER records.fail_budget_receipt;"); }
        var receipt = session.Commit(ExecutionStopReason.Completed);
        Assert.Equal(physical, receipt);
        var terminal = Assert.IsType<RunBudgetObservation>(fixture.Budget(occurrence.Frozen.RunId));
        Assert.Equal(before.Ordinal + 1, terminal.Ordinal);
        Assert.Equal(receipt.SourceReceiptDigest, terminal.TerminalReceiptDigest);
        Assert.True(terminal.ConsumedMilliseconds >= before.ConsumedMilliseconds);
        fixture.RequireActualClosure(occurrence.Frozen.StartOperationId);
        RunBudgetInventory.Verify(fixture.Source.Instance, fixture.Source.Database, new());
    }

    [Theory(DisplayName = "Appending A Budget Rejects A Changed Prefix, A Changed New Row, Or An Orphan History")]
    [InlineData("prefix")]
    [InlineData("new-row")]
    [InlineData("orphan")]
    public async Task PreservesBudgetAppendEvidence(string damage)
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        var occurrence = fixture.CommitOnly(); await fixture.Scheduler.TickAsync();
        var run = occurrence.Frozen.RunId;
        var accepted = Assert.IsType<RunBudgetObservation>(fixture.Budget(run));
        Assert.Equal(1, accepted.Ordinal);
        var next = accepted with { Ordinal = 2, PreviousOperationId = RunBudgetRows.Identity(accepted).OperationId };
        if (damage == "orphan")
        {
            fixture.Sql($"DELETE FROM main.execution_run_budget_states WHERE run_id='{run:D}'; " +
                "DELETE FROM main.execution_run_budget_observations WHERE ordinal=0; " +
                $"DELETE FROM main.operations WHERE operation_id='{accepted.PreviousOperationId:D}';");
            next = accepted with { Ordinal = 0, PreviousOperationId = null, JournalId = null };
        }
        else
        {
            var ordinal = damage == "prefix" ? 0 : 2;
            fixture.Sql("CREATE TRIGGER main.change_budget_observation AFTER INSERT ON execution_run_budget_observations " +
                $"BEGIN UPDATE execution_run_budget_observations SET document_json='{{}}' WHERE run_id=NEW.run_id AND ordinal={ordinal}; END;");
        }
        var count = fixture.Count("execution_run_budget_observations");
        var operations = fixture.Count("operations");
        var error = Assert.Throws<IOException>(() => fixture.Source.Database.Write((connection, transaction) =>
        { RunBudgetRows.Append(connection, transaction, next); return true; }));
        Assert.Equal("execution_run_budget_unbound", error.Message);
        Assert.Equal(count, fixture.Count("execution_run_budget_observations"));
        Assert.Equal(operations, fixture.Count("operations"));
        Assert.Equal(damage == "orphan" ? null : accepted, fixture.Budget(run));
    }

    [Fact(DisplayName = "A New Real Instance Lease Preserves The Deadline But Cannot Invent Uncheckpointed Budget Continuity")]
    public async Task PreservesReopenedDeadline()
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        var occurrence = fixture.CommitOnly(); await fixture.Scheduler.TickAsync();
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        fixture.RequireActualClosure(occurrence.Frozen.StartOperationId);
        var original = Assert.IsType<RunBudgetObservation>(fixture.Budget(receipt.Facts.RunId));
        fixture.Source.Lease.Dispose();
        using var successor = InstanceLease.Acquire(fixture.Source.Instance, new());
        Assert.Equal(original, fixture.Budget(receipt.Facts.RunId));
        var error = Assert.Throws<IOException>(() => successor.RunBudgets.ReadForRetry(fixture.Source.Database, receipt, TimeProvider.System));
        Assert.Equal("execution_retry_budget_continuity_unknown", error.Message);
    }

    [Theory(DisplayName = "Read Only Startup Rejects Missing Minute Facts Without Rewriting The Original Database")]
    [InlineData("tail")]
    [InlineData("header")]
    [InlineData("all-items")]
    [InlineData("watermark")]
    public void PreservesMissingMinutes(string damage)
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        _ = fixture.CommitOnly();
        fixture.Sql(damage switch
        {
            "tail" => "UPDATE main.schedule_minute_occurrences SET position=position+1;",
            "all-items" => "DELETE FROM main.schedule_minute_occurrences;",
            "header" => "PRAGMA foreign_keys=OFF; DELETE FROM main.schedule_minute_claims;",
            _ => "UPDATE main.scheduling_watermark SET highest_utc_minute=highest_utc_minute-1 WHERE singleton=1;"
        });
        var before = fixture.Fingerprints();
        var error = Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Source.Instance,
            Path.Combine(fixture.Source.Root, "schedule-diagnostic"), new JournalStartupAdmission(fixture.Source.Instance, new())));
        Assert.Equal(damage == "header" ? "database_foreign_key_failure" : "schedule_minute_metadata_unbound", error.Message);
        Assert.Equal(before.OrderBy(pair => pair.Key), fixture.Fingerprints().OrderBy(pair => pair.Key));
    }

    [Fact(DisplayName = "A Real Minute Boundary Creates One Durable Occurrence And An Actual Stopped Native Attempt")]
    public async Task RunsDurableOccurrences()
    {
        using var fixture = new ScheduleNativeFixture(nextMinute: true);
        var target = fixture.TriggerUtc;
        await fixture.Scheduler.TickAsync(); Assert.Empty(fixture.Store.ReadWaiting(64)); Assert.Equal(0, fixture.Base.Inbox.PendingCount);
        var waiting = Stopwatch.StartNew();
        var remaining = target - DateTimeOffset.UtcNow;
        Assert.True(remaining > TimeSpan.Zero);
        await Task.Delay(remaining + TimeSpan.FromMilliseconds(150));
        Assert.True(DateTimeOffset.UtcNow >= target); Assert.True(waiting.Elapsed > TimeSpan.Zero);
        await fixture.Scheduler.TickAsync();
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT occurrence_id FROM main.scheduled_occurrences;";
        var id = Guid.ParseExact((string)command.ExecuteScalar()!, "D");
        var occurrence = ScheduleMinuteRows.ReadOccurrence(connection, transaction, id, fixture.Source.Instance.InstanceId);
        Assert.Equal(target.ToUnixTimeSeconds() / 60, occurrence.Frozen.TriggerUtcMinute);
        Assert.Equal(occurrence.Frozen.RunId, receipt.Facts.RunId);
        fixture.RequireActualClosure(occurrence.Frozen.StartOperationId);
        Assert.Equal(1, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Null(await fixture.Consumer.RunNextAsync());
    }
}
