using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Automation;
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
        Assert.ThrowsAny<Exception>(() => StateDatabase.OpenExisting(fixture.Source.Instance,
            Path.Combine(fixture.Source.Root, "schedule-diagnostic"), new JournalStartupAdmission(fixture.Source.Instance, new())));
        Assert.Equal(before.OrderBy(pair => pair.Key), fixture.Fingerprints().OrderBy(pair => pair.Key));
    }

    [Fact(DisplayName = "A Real Minute Boundary Creates One Durable Occurrence And An Actual Stopped Native Attempt")]
    public async Task RunsDurableOccurrences()
    {
        using var fixture = new ScheduleNativeFixture(nextMinute: true);
        var target = fixture.TriggerUtc;
        await fixture.Scheduler.TickAsync(); Assert.Empty(fixture.Store.ReadWaiting(64)); Assert.Equal(0, fixture.Base.Inbox.PendingCount);
        var waiting = Stopwatch.StartNew();
        await Task.Delay(target - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150));
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
