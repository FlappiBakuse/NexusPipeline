using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ActionDispositionTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "Authorized Effect Entry Reuses The Verified Countdown And Still Rechecks The Write")]
    public async Task ReadsAuthorizedStateOncePerBoundary()
    {
        var clock = new CompletionControlledClock();
        await using var fixture = new CompletionActionFixture(clock: clock, observeCompletion: false);
        var receipt = await fixture.RunToReceipt();
        var source = fixture.Source;
        var database = source.Database;
        var finality = new ExecutionRunFinality(source.Instance, database, new());
        var intents = new CompletionIntentSource(source.Instance, database, new(), source.Lease, finality);
        var candidates = new CompletionCandidateStore(database, source.Lease, intents, Guid.NewGuid(), clock);
        var candidate = Assert.IsType<BoundCompletionIntent>(candidates.Accept(receipt));
        var store = new CompletionActionStore(database, source.Lease, candidates, intents, fixture.Approval, clock,
            new(database, source.Lease, fixture.Base.Resources, fixture.Base.Lifecycle));
        store.BeginPending();
        var operation = Assert.IsType<Guid>(store.PendingOperationId);
        using var prepared = Assert.IsAssignableFrom<IPreparedCompletionAction>(
            store.Prepare(operation, CompletionActionStore.HostSubject, [candidate.Intent]));
        Assert.Equal(CompletionEffectPhase.Countdown, prepared.Snapshot.Phase);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, allowPooling: false, domains: DatabaseDomain.Runtime))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        {
            var queries = new List<string>();
            SQLitePCL.strdelegate_trace trace = (_, sql) => queries.Add(sql);
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
            try { Assert.NotNull(CompletionActionRows.Read(connection, transaction, operation, CompletionActionStore.HostSubject)); }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace?)null, null); }
            Assert.Equal(4, queries.Count);
            foreach (var sql in queries)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "EXPLAIN QUERY PLAN " + sql;
                using var planner = command.ExecuteReader();
                while (planner.Read()) output.WriteLine(planner.GetString(3));
            }
        }

        clock.Advance(TimeSpan.FromSeconds(60));
        fixture.Power.Disposition = CompletionNativeDisposition.Rejected;
        var previous = Console.Error;
        using var capture = new StringWriter();
        (CompletionActionSnapshot Snapshot, CompletionNativeResult Result)? result;
        TimeSpan elapsed, cpu;
        long allocated;
        try
        {
            Console.SetError(capture);
            using var profile = StartupProfile.Begin("completion-authorized-read-test");
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            result = store.InvokeAuthorized(prepared, default, state => fixture.Power.Invoke(state.Frozen.Action));
            elapsed = Stopwatch.GetElapsedTime(started);
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            cpu = process.TotalProcessorTime - cpuBefore;
        }
        finally { Console.SetError(previous); }
        using var report = JsonDocument.Parse(capture.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"phase\":\"completion-authorized-read-test\"", StringComparison.Ordinal)));
        var steps = report.RootElement.GetProperty("steps").EnumerateArray().ToDictionary(
            step => step.GetProperty("name").GetString()!, step => step.GetProperty("count").GetInt64());
        var requires = steps["completion.require"];
        var selects = steps["completion.action.select"];
        var identities = steps["completion.identity"];
        output.WriteLine($"completion-authorized requires={requires} select-including-cas={selects} identities={identities} elapsed-ms={elapsed.TotalMilliseconds:F3} process-cpu-ms={cpu.TotalMilliseconds:F3} process-allocated-bytes={allocated}");

        Assert.NotNull(result);
        Assert.Equal(CompletionEffectPhase.Entered, result.Value.Snapshot.Phase);
        Assert.Equal(CompletionNativeDisposition.Rejected, result.Value.Result.Disposition);
        Assert.Equal(CompletionEffectPhase.Entered, store.Read(operation, CompletionActionStore.HostSubject)!.Phase);
        Assert.Equal("completion_countdown_pending", Assert.Throws<IOException>(() =>
            store.InvokeAuthorized(prepared, default, state => fixture.Power.Invoke(state.Frozen.Action))).Message);
        Assert.Equal(1, fixture.Power.Calls);
        store.RecordResult(prepared, CompletionEffectPhase.Failed, result.Value.Result.ReasonCode);
        Assert.Equal(CompletionEffectPhase.Failed, store.Read(operation, CompletionActionStore.HostSubject)!.Phase);
        Assert.Equal(HostLifecyclePhase.Running, fixture.Base.Lifecycle.Phase);
        Assert.Equal(receipt, new AttemptReceiptStore(database).Read(receipt.Facts.AttemptId));
        Assert.Equal(2, requires);
        Assert.Equal(12, selects);
        Assert.Equal(3, identities);
    }

    [Theory(DisplayName = "A Controlled Acknowledgement Uses The Frozen Action And Hibernation Return Reopens Admission")]
    [InlineData(AutomationCompletionAction.Hibernate, 0)]
    [InlineData(AutomationCompletionAction.Restart, 1)]
    public async Task ReopensReturnedActions(
        AutomationCompletionAction action, int exitRequests)
    {
        var clock = new CompletionControlledClock();
        await using var fixture = new CompletionActionFixture(action, clock);
        var (_, countdown) = await fixture.RunToCountdown();
        clock.Advance(TimeSpan.FromSeconds(60));
        await fixture.WaitFor(CompletionEffectPhase.Accepted, countdown.OperationId);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(3) &&
            (action == AutomationCompletionAction.Hibernate ? fixture.Base.Lifecycle.Phase != HostLifecyclePhase.Running : fixture.Exit.Requests != 1))
            await Task.Delay(10);
        Assert.Equal(action, fixture.Power.Action); Assert.Equal(1, fixture.Power.Calls);
        Assert.Equal(exitRequests, fixture.Exit.Requests);
        if (action == AutomationCompletionAction.Hibernate)
        {
            using var next = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [new("unrelated", ResourceClaimMode.Mutation)]).Lease;
            Assert.NotNull(next);
        }
        else Assert.Equal(HostLifecyclePhase.ClosePermitted, fixture.Base.Lifecycle.Phase);
    }

    [Fact(DisplayName = "A Rejected System Request Records Failure And Reopens Admission Without Changing The Run")]
    public async Task RecordsRejectedActions()
    {
        var clock = new CompletionControlledClock();
        await using var fixture = new CompletionActionFixture(clock: clock);
        fixture.Power.Disposition = CompletionNativeDisposition.Rejected;
        var (receipt, countdown) = await fixture.RunToCountdown();
        clock.Advance(TimeSpan.FromSeconds(60)); await fixture.WaitFor(CompletionEffectPhase.Failed, countdown.OperationId);
        Assert.Equal(1, fixture.Power.Calls); Assert.Equal(0, fixture.Exit.Requests);
        Assert.Equal(HostLifecyclePhase.Running, fixture.Base.Lifecycle.Phase);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "Exit Requests Only Graceful Host Exit Without Waiting Or Invoking Any Power Api")]
    public async Task RequestsGracefulExit()
    {
        var clock = new CompletionControlledClock();
        await using var fixture = new CompletionActionFixture(AutomationCompletionAction.Exit, clock);
        var receipt = await fixture.RunToReceipt();
        var accepted = await fixture.WaitFor(CompletionEffectPhase.Accepted, advanceClock: true);
        Assert.Equal(accepted.AcceptedAtUtc, accepted.NotBeforeUtc);
        Assert.Equal(1, fixture.Exit.Requests); Assert.Equal(0, fixture.Power.Calls);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Completion.CancelAsync(accepted.OperationId, CompletionActionStore.HostSubject, default));
    }
}
