using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScheduleWaitingTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Each Distinct Due Binding Is Accepted Once In A Newly Committed Minute")]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(64)]
    public async Task MeasuresDueAdmission(int count)
    {
        var clock = new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow);
        using var fixture = new ScheduleNativeFixture(clock);
        var source = fixture.Source;
        var expected = SeedDueSchedules(fixture, count);
        Assert.Equal(0, fixture.Count("schedule_minute_claims"));
        Assert.Equal(0, fixture.Count("scheduled_occurrences"));
        Assert.Empty(fixture.Store.ReadWaiting(count));

        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var admission = new MeasuredAdmission(new ScheduledExecutionAdmission(source.Instance, source.Database, source.Lease,
            fixture.Base.Service, clock), started);
        var scheduler = new ScheduleRuntimeService(fixture.Store, admission, clock);
        try
        {
            await scheduler.TickAsync();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            process.Refresh();
            var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var workingSetBytes = process.WorkingSet64;
            Assert.Equal(count, fixture.Base.Inbox.PendingCount);
            Assert.Equal(expected.Keys.Order(), admission.Accepted.Select(item => item.ScheduleId).Order());
            Assert.Equal(count, admission.Accepted.Select(item => item.OperationId).Distinct().Count());
            Assert.Equal(count, admission.Accepted.Select(item => item.JournalId).Distinct().Count());
            Assert.Equal(1, fixture.Count("schedule_minute_claims"));
            Assert.Equal(count, fixture.Count("scheduled_occurrences"));
            await scheduler.TickAsync();
            Assert.Equal(count, admission.Accepted.Count);
            Assert.Equal(count, fixture.Base.Inbox.PendingCount);
            Assert.Empty(fixture.Store.ReadWaiting(count));

            foreach (var item in admission.Accepted)
            {
                var binding = expected[item.ScheduleId];
                Assert.Equal(binding.QueueId, item.QueueId);
                using var owned = fixture.Base.Take(item.OperationId);
                Assert.Equal(item.JournalId, owned.Receipt.JournalId);
                var step = Assert.Single(owned.Prepared.Total.Plan.ReadWire().Steps);
                Assert.Equal(binding.BindingId.ToString("D"), step.BindingId);
            }
            Assert.Equal(0, fixture.Base.Count("journal_writer_jobs"));
            output.WriteLine(JsonSerializer.Serialize(new { chain = "M17", count, elapsed, cpuMilliseconds, allocatedBytes, workingSetBytes,
                counterScope = "test host process during one scheduler tick", admissions = admission.Accepted }));
        }
        finally { await scheduler.StopAsync(); }
    }

    [Theory(DisplayName = "Scheduled Runs Append Accepted And Terminal Budget Observations Bound To Their Original Deadline And Receipt")]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public async Task MeasuresScheduledBudgetLedger(int count)
    {
        using var fixture = new ScheduleNativeFixture(new ScheduleNativeFixture.FrozenScheduleClock(DateTimeOffset.UtcNow));
        const int totalSeconds = 900;
        var source = fixture.Source;
        var expected = SeedDueSchedules(fixture, count, totalSeconds);
        var phase = Stopwatch.StartNew();
        var scheduled = fixture.Store.CommitCurrentMinute();
        var scheduleMs = phase.Elapsed.TotalMilliseconds;
        Assert.Equal(expected.Keys.Order(), scheduled.Select(occurrence => occurrence.Frozen.ScheduleId).Order());
        Assert.All(scheduled, occurrence => Assert.Equal(expected[occurrence.Frozen.ScheduleId].QueueId, occurrence.Frozen.QueueId));
        Assert.Equal(0, fixture.Count("execution_run_budget_observations"));

        var admissionBegan = DateTimeOffset.UtcNow;
        phase.Restart();
        await fixture.Scheduler.TickAsync();
        var acceptMs = phase.Elapsed.TotalMilliseconds;
        var admissionEnded = DateTimeOffset.UtcNow;
        Assert.Equal(count, fixture.Base.Inbox.PendingCount);
        Assert.Equal(count * 2, fixture.Count("execution_run_budget_observations"));
        var accepted = scheduled.ToDictionary(occurrence => occurrence.Frozen.RunId,
            occurrence => Assert.IsType<RunBudgetObservation>(fixture.Budget(occurrence.Frozen.RunId)));

        phase.Restart();
        var receipts = new Dictionary<Guid, AttemptReceipt>();
        for (var index = 0; index < count; index++)
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.True(receipts.TryAdd(receipt.Facts.RunId, receipt));
        }
        var completeMs = phase.Elapsed.TotalMilliseconds;
        Assert.Equal(accepted.Keys.Order(), receipts.Keys.Order());
        Assert.Equal(count * 3, fixture.Count("execution_run_budget_observations"));
        Assert.Equal(count, fixture.Count("execution_run_budget_states"));
        Assert.Equal(count, fixture.Count("journal_writer_job_roots"));
        Assert.Equal(0, fixture.Base.Inbox.PendingCount);
        await fixture.Scheduler.TickAsync();
        Assert.Null(await fixture.Consumer.RunNextAsync());
        Assert.Equal(count * 3, fixture.Count("execution_run_budget_observations"));

        var reopened = StateDatabase.ObserveMaintenancePeer(source.Instance);
        using (var connection = DatabaseConnections.Open(reopened.DatabasePath, readOnly: true, allowPooling: false, domains: DatabaseDomain.Runtime))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        {
            foreach (var occurrence in scheduled)
            {
                var frozen = occurrence.Frozen;
                var receipt = receipts[frozen.RunId];
                Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
                Assert.Equal(frozen.StartOperationId, receipt.OperationId);
                var state = AttemptWriterState.Read(source.Instance, source.Database, new(), receipt.JournalId);
                Assert.Equal(expected[frozen.ScheduleId].BindingId.ToString("D"), Assert.Single(state.Total.Steps).BindingId);
                var deadline = DateTimeOffset.Parse(state.Total.RetryPolicy.DeadlineUtc, System.Globalization.CultureInfo.InvariantCulture);
                fixture.RequireActualClosure(receipt.OperationId);
                var observations = new List<RunBudgetObservation>();
                var terminal = RunBudgetRows.Read(connection, transaction, frozen.RunId, observations.Add);
                Assert.Collection(observations,
                    first =>
                    {
                        Assert.Equal(0, first.Ordinal);
                        Assert.InRange(first.FirstPreparedAtUtc, admissionBegan, admissionEnded);
                        Assert.Equal(first.FirstPreparedAtUtc.AddSeconds(totalSeconds), deadline);
                        Assert.Null(first.PreviousOperationId);
                        Assert.Null(first.JournalId);
                        Assert.Null(first.TerminalReceiptDigest);
                    },
                    prepared =>
                    {
                        Assert.Equal(1, prepared.Ordinal);
                        Assert.Equal(accepted[frozen.RunId], prepared);
                        Assert.Equal(receipt.JournalId, prepared.JournalId);
                        Assert.Null(prepared.TerminalReceiptDigest);
                    },
                    final =>
                    {
                        Assert.Equal(2, final.Ordinal);
                        Assert.Equal(receipt.JournalId, final.JournalId);
                        Assert.Equal(receipt.SourceReceiptDigest, final.TerminalReceiptDigest);
                        Assert.Equal(final, terminal);
                    });
                foreach (var observation in observations)
                {
                    Assert.Equal(frozen.RunId, observation.RunId);
                    Assert.Equal(frozen.OccurrenceId, observation.OccurrenceId);
                    Assert.Equal(frozen.StartOperationId, observation.StartOperationId);
                    Assert.Equal(occurrence.SubjectId, observation.SubjectId);
                    Assert.Equal(totalSeconds * 1000, observation.TotalMilliseconds);
                    Assert.Equal(deadline, observation.DeadlineUtc);
                    Assert.Equal(observations[0].FirstPreparedAtUtc, observation.FirstPreparedAtUtc);
                }
                Assert.Equal(observations.Select(item => item.ConsumedMilliseconds).Order(), observations.Select(item => item.ConsumedMilliseconds));
            }
        }
        phase.Restart();
        RunBudgetInventory.Verify(source.Instance, reopened, new());
        var auditMs = phase.Elapsed.TotalMilliseconds;
        output.WriteLine(JsonSerializer.Serialize(new { chain = "M18", count, scheduleMs, acceptMs, completeMs, auditMs, totalSeconds,
            acceptedRows = count * 2, completedRows = count * 3,
            counterScope = "fresh scheduled runs, one actual accepted and terminal attempt per run; timings cover scheduling, admission, consumption and read-only ledger verification separately" }));
    }

    private static Dictionary<Guid, (Guid QueueId, Guid BindingId)> SeedDueSchedules(ScheduleNativeFixture fixture, int count,
        int? totalSeconds = null)
    {
        var source = fixture.Source;
        var catalog = source.Definitions.Read();
        var originalSchedule = Assert.Single(catalog.Schedules);
        var template = catalog.Scripts.Single(script => script.Id == Assert.Single(catalog.Queues).ScriptIds[0]);
        if (totalSeconds is { } timeout)
            template = template with { Limits = template.Limits with { TotalTimeoutSeconds = timeout } };
        var scripts = new List<AutomationScript>();
        var users = new List<AutomationUser>();
        var bindings = new List<AutomationBinding>();
        var queues = new List<AutomationQueue>();
        var schedules = new List<AutomationSchedule> { originalSchedule with { IsEnabled = false } };
        for (var index = 0; index < count; index++)
        {
            var script = template with { Id = Guid.NewGuid(), Revision = 0, Name = $"Due script {index}", Order = catalog.Scripts.Length + index };
            var user = new AutomationUser(Guid.NewGuid(), 0, $"Due user {index}", catalog.Users.Length + index);
            var binding = new AutomationBinding(Guid.NewGuid(), 0, user.Id, script.Id, true, 0);
            var queue = new AutomationQueue(Guid.NewGuid(), 0, $"Due queue {index}", [script.Id], AutomationCompletionAction.None);
            var schedule = originalSchedule with { Id = Guid.NewGuid(), Revision = 0, QueueId = queue.Id, Order = index + 1 };
            scripts.Add(script); users.Add(user); bindings.Add(binding); queues.Add(queue); schedules.Add(schedule);
        }
        var change = new AutomationChanges(catalog.Revision, [], [.. scripts], [.. users], [.. bindings], [.. queues], [.. schedules], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "due-admission-fixture", change), change, fixture.Clock.GetUtcNow());
        return schedules.Skip(1).Zip(bindings).ToDictionary(pair => pair.First.Id, pair => (pair.First.QueueId, pair.Second.Id));
    }

    private sealed record MeasuredOccurrence(Guid ScheduleId, Guid QueueId, Guid OperationId, Guid JournalId, double AcceptedMilliseconds);

    private sealed class MeasuredAdmission(IScheduledExecutionAdmission inner, long started) : IScheduledExecutionAdmission
    {
        internal List<MeasuredOccurrence> Accepted { get; } = [];

        public async ValueTask<IExecutionAdmissionReceipt> StartAsync(ScheduledOccurrence occurrence, CancellationToken cancellation)
        {
            var receipt = await inner.StartAsync(occurrence, cancellation);
            Assert.True(receipt.Acceptance.IsNew);
            Accepted.Add(new(occurrence.Frozen.ScheduleId, occurrence.Frozen.QueueId, receipt.Acceptance.OperationId,
                receipt.JournalId, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            return receipt;
        }
    }

    [Theory(DisplayName = "A Due Minute Uses The Shared Waiting Limit Without Rescanning Finished Occurrences")]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundsDueMinuteWaitingCapacity(bool full)
    {
        var clock = new ScheduleNativeFixture.FrozenScheduleClock(PlanBuilderFixture.CreatedAt);
        using var fixture = new ScheduleNativeFixture(clock);
        var source = fixture.Source;
        source.Lease.BindDatabase(source.Database);
        var path = source.Database.DatabasePath;
        SQLitePCL.sqlite3 writerHandle;
        var batches = 0;
        var minuteWriteReached = false;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
            minuteWriteReached |= sql.StartsWith("BEGIN IMMEDIATE", StringComparison.Ordinal);
        using (var writer = DatabaseConnections.Open(path))
        {
            using var setup = writer.BeginTransaction(deferred: false);
            ScheduleCapacityHistory.Seed(writer, setup);
            if (full)
            {
                using var removeStarts = DatabaseConnections.Command(writer, setup,
                    "DELETE FROM main.operations WHERE operation_id BETWEEN $0 AND $1;",
                    "923cebdf-3954-448f-b378-000000000001", "923cebdf-3954-448f-b378-000000004096");
                Assert.Equal(4096, removeStarts.ExecuteNonQuery());
            }
            ScheduledProviderRows.RequireCapacity(writer, setup, 0);
            setup.Commit();
            writerHandle = writer.Handle!;
            SQLitePCL.raw.sqlite3_progress_handler(writerHandle, 100, _ => { batches++; return 0; }, null);
            SQLitePCL.raw.sqlite3_trace(writerHandle, trace, null);
        }

        ScheduledOccurrence? occurrence = null;
        try
        {
            if (full)
                Assert.Equal("schedule_waiting_capacity", Assert.Throws<IOException>(() => fixture.Store.CommitCurrentMinute()).Message);
            else occurrence = Assert.Single(fixture.Store.CommitCurrentMinute());
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(writerHandle, (SQLitePCL.strdelegate_trace?)null, null);
            SQLitePCL.raw.sqlite3_progress_handler(writerHandle, 0, null, null);
        }
        Assert.True(minuteWriteReached);
        Assert.Equal(full ? 12000 : 12001, fixture.Count("scheduled_occurrences"));
        Assert.Equal(full ? 3 : 4, fixture.Count("schedule_minute_claims"));
        if (!full)
        {
            Assert.Equal(fixture.ScheduleId, occurrence!.Frozen.ScheduleId);
            Assert.Equal(occurrence, Assert.Single(fixture.Store.ReadWaiting(1)));
            Assert.Equal(occurrence, Assert.Single(fixture.Store.CommitCurrentMinute()));
        }
        output.WriteLine($"due minute: 12000 historical occurrences, full={full}, {batches} SQLite progress callbacks at 100 VM instructions");
        Assert.InRange(batches, 0, 60);
    }

    [Fact(DisplayName = "A Due Minute Reuses An Unchanged Catalog And Rejects An Edit Before Its Write Lock")]
    public void ChecksCatalogAtMinuteCommit()
    {
        var clock = new ScheduleNativeFixture.FrozenScheduleClock(PlanBuilderFixture.CreatedAt);
        using var fixture = new ScheduleNativeFixture(clock);
        var source = fixture.Source;
        source.Lease.BindDatabase(source.Database);
        var path = source.Database.DatabasePath;
        var catalogRevision = source.Definitions.ReadRevision();
        using (var writer = DatabaseConnections.Open(path))
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "SELECT seq FROM pragma_database_list WHERE name='automation';";
            var automation = Assert.IsType<long>(command.ExecuteScalar());
            command.CommandText = "EXPLAIN BEGIN IMMEDIATE;";
            using var plan = command.ExecuteReader();
            var reservesAutomation = false;
            while (plan.Read())
                reservesAutomation |= plan.GetString(1) == "Transaction" && plan.GetInt64(2) == automation && plan.GetInt64(3) == 1;
            Assert.True(reservesAutomation);
            output.WriteLine($"due-minute writer-reserves-automation={reservesAutomation}");
        }

        var changed = TraceWrite(() => Assert.Equal("schedule_source_revision_conflict",
            Assert.Throws<IOException>(() => fixture.Store.CommitCurrentMinute()).Message), disableBeforeBegin: true);
        Assert.Equal(catalogRevision, source.Definitions.ReadRevision());
        Assert.Equal(0, fixture.Store.ReadCheckpoint().Watermark.Revision);
        Assert.Equal(0, fixture.Count("schedule_minute_claims"));
        Assert.Equal(0, fixture.Count("scheduled_occurrences"));
        fixture.Sql("UPDATE automation.schedules SET enabled=1;");

        int catalogBytes;
        using (var connection = DatabaseConnections.Open(path, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation))
        using (var transaction = DatabaseConnections.BeginRead(connection))
            catalogBytes = BoundedJson.Serialize(AutomationRows.Read(connection, transaction)).Length;

        ScheduledOccurrence? accepted = null;
        var stable = TraceWrite(() => accepted = Assert.Single(fixture.Store.CommitCurrentMinute()));
        Assert.NotNull(accepted);
        Assert.Equal(catalogRevision, accepted.Frozen.CatalogRevision);
        Assert.Equal(ScheduleOccurrencePhase.WaitingForAdmission, accepted.Phase);
        Assert.Equal(accepted, Assert.Single(fixture.Store.CommitCurrentMinute()));
        Assert.Equal(1, fixture.Count("schedule_minute_claims"));
        Assert.Equal(1, fixture.Count("scheduled_occurrences"));
        output.WriteLine($"due-minute changed-catalog-selects={changed.Length} stable-catalog-selects={stable.Length} catalog-document-bytes={catalogBytes}");
        Assert.Equal(17, changed.Length);
        Assert.Empty(stable);

        string[] TraceWrite(Action commit, bool disableBeforeBegin = false)
        {
            var statements = new List<string>();
            Exception? writeError = null;
            var injected = false;
            SQLitePCL.strdelegate_trace capture = (_, sql) =>
            {
                statements.Add(sql);
                if (!disableBeforeBegin || injected || !sql.StartsWith("BEGIN IMMEDIATE", StringComparison.Ordinal)) return;
                injected = true;
                try
                {
                    // This second SQLite connection commits after the scan, before the writer acquires its reservation.
                    using var other = DatabaseConnections.Open(path, allowPooling: false, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation);
                    using var command = DatabaseConnections.Command(other, null,
                        "UPDATE automation.schedules SET enabled=0 WHERE schedule_id=$0;", fixture.ScheduleId.ToString("D"));
                    if (command.ExecuteNonQuery() != 1) throw new IOException("Fixture schedule did not change");
                }
                catch (Exception error) { writeError = error; }
            };
            SQLitePCL.sqlite3 handle;
            using (var writer = DatabaseConnections.Open(path))
            {
                handle = writer.Handle!;
                SQLitePCL.raw.sqlite3_trace(handle, capture, null);
            }
            var started = Stopwatch.GetTimestamp();
            try { commit(); }
            finally { SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null); }
            var elapsed = Stopwatch.GetElapsedTime(started);
            Assert.Equal(disableBeforeBegin, injected);
            Assert.Null(writeError);
            output.WriteLine($"due-minute changed={injected} traced-writer-statements={statements.Count} elapsed-ms={elapsed.TotalMilliseconds:F3}");
            return statements.Where(sql => sql.Contains("FROM automation.", StringComparison.Ordinal) &&
                !sql.Contains(".sqlite_schema", StringComparison.Ordinal)).ToArray();
        }
    }

    [Fact(DisplayName = "Idle Minutes Reuse The Verified Catalog And Detect Changes Without A New Catalog Revision")]
    public void RefreshesIdleCatalog()
    {
        var clock = new MovingScheduleClock(PlanBuilderFixture.CreatedAt);
        using var fixture = new ScheduleNativeFixture(clock);
        var source = fixture.Source;
        source.Lease.BindDatabase(source.Database);
        const DatabaseDomain domains = DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.System;
        var cold = TraceIdle();
        var warm = TraceIdle();
        Assert.Equal(17, CatalogReads(cold).Length);
        using (var reader = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: domains))
        using (var transaction = DatabaseConnections.BeginRead(reader))
        {
            var coldRows = CatalogReads(cold).Sum(sql => Rows(reader, transaction, sql));
            var warmRows = CatalogReads(warm).Sum(sql => Rows(reader, transaction, sql));
            output.WriteLine($"idle-catalog cold-selects={CatalogReads(cold).Length} cold-rows={coldRows} warm-selects={CatalogReads(warm).Length} warm-rows={warmRows} cold-role-statements={cold.Length} warm-role-statements={warm.Length}");
            using var explain = DatabaseConnections.Command(reader, transaction,
                "EXPLAIN QUERY PLAN " + CatalogReads(cold).Single(sql => sql.Contains("FROM automation.schedules ORDER BY", StringComparison.Ordinal)));
            using var plan = explain.ExecuteReader();
            while (plan.Read()) output.WriteLine(plan.GetString(3));
        }

        for (var i = 0; i < 5; i++) Idle();
        const int iterations = 32;
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++) Idle();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var cpu = process.TotalProcessorTime - cpuBefore;
        output.WriteLine($"idle-catalog warm-minutes={iterations} elapsed-ms={elapsed.TotalMilliseconds:F3} process-cpu-ms={cpu.TotalMilliseconds:F3} process-allocated-bytes={allocated}");

        var revision = source.Definitions.ReadRevision();
        var original = Assert.Single(source.Definitions.Read().Schedules);
        fixture.Sql("UPDATE automation.schedules SET weekdays_json='[8]';");
        Assert.Equal(revision, source.Definitions.ReadRevision());
        Assert.Equal("automation_schedule_invalid", Assert.Throws<AutomationDefinitionException>(Idle).Message);
        fixture.Sql("UPDATE automation.schedules SET weekdays_json='[" + original.Weekdays.Single() + "]';");
        Idle();
        var now = clock.GetUtcNow();
        var due = original with { LocalMinute = now.Hour * 60 + now.Minute, IsEnabled = false };
        var disabled = Save(due);
        Assert.Empty(fixture.Store.CommitCurrentMinute());
        Assert.Equal(0, fixture.Store.ReadCheckpoint().Watermark.Revision);
        var enabled = Save(disabled with { IsEnabled = true });
        var accepted = Assert.Single(fixture.Store.CommitCurrentMinute());
        Assert.Equal(enabled.Revision, accepted.Frozen.ScheduleRevision);
        Assert.Equal(ScheduleOccurrencePhase.WaitingForAdmission, accepted.Phase);
        Assert.Equal(accepted, Assert.Single(fixture.Store.CommitCurrentMinute()));
        Assert.Equal(1, fixture.Count("schedule_minute_claims"));
        Assert.Empty(CatalogReads(warm));

        AutomationSchedule Save(AutomationSchedule schedule)
        {
            var changes = new AutomationChanges(source.Definitions.ReadRevision(), [], [], [], [], [], [schedule], []);
            source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "idle-schedule", changes), changes, clock.GetUtcNow());
            return Assert.Single(source.Definitions.Read().Schedules);
        }

        void Idle()
        {
            clock.Advance();
            Assert.Empty(fixture.Store.CommitCurrentMinute());
        }

        string[] TraceIdle()
        {
            var statements = new List<string>();
            SQLitePCL.sqlite3 handle;
            using (var reader = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: domains))
            {
                handle = reader.Handle!;
                SQLitePCL.strdelegate_trace capture = (_, sql) => statements.Add(sql);
                SQLitePCL.raw.sqlite3_trace(handle, capture, null);
            }
            try { Idle(); }
            finally { SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null); }
            return statements.ToArray();
        }

        static string[] CatalogReads(string[] sql) => sql.Where(statement => statement.Contains("FROM automation.", StringComparison.Ordinal) &&
            !statement.Contains(".sqlite_schema", StringComparison.Ordinal)).ToArray();

        static int Rows(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using var command = DatabaseConnections.Command(connection, transaction, sql);
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read()) count++;
            return count;
        }
    }

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
