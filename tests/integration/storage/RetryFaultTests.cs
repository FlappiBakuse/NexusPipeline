using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetryFaultTests
{
    [Fact(DisplayName = "Repeated Real Resource Conflicts Keep The Same Waiting Successor And Never Reset Its Live Budget")]
    public async Task KeepsWaitingBudget()
    {
        await using var fixture = await Create("retry-interrupted");
        var first = await fixture.RunInterruptedAsync(); var retries = Retry(fixture);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        var beforeBudget = pending.TimeBudget.Read();
        var operation = pending.Frozen.NextOperationId;
        var sourceBytes = new JournalStore(fixture.Source.Instance, new()).Read(first.JournalId).Export();
        var counts = fixture.Base.Counts();
        var blocked = fixture.Base.Resources.TryAcquire(Guid.NewGuid(),
            [new("automation:" + fixture.Source.OtherScriptId.ToString("D"), ResourceClaimMode.Mutation)]);
        using (var held = Assert.IsType<ResourceOperationLease>(blocked.Lease))
        {
            for (var tick = 0; tick < 3; tick++) retries.Tick(DateTimeOffset.UtcNow);
            Assert.Equal(counts, fixture.Base.Counts());
            Assert.Equal(0, fixture.Base.Inbox.PendingCount);
            using var stillWaiting = Assert.IsType<PreparedExecutionRetry>(retries.ReadWaiting(first.Facts.RunId));
            Assert.Equal(operation, stillWaiting.Frozen.NextOperationId);
            Assert.Equal(pending.Frozen.DeadlineUtc, stillWaiting.Frozen.DeadlineUtc);
            var after = stillWaiting.TimeBudget.Read();
            Assert.True(after.ConsumedMilliseconds >= beforeBudget.ConsumedMilliseconds);
            Assert.True(after.Remaining <= beforeBudget.Remaining);
            Assert.Single(File.ReadAllLines(fixture.Marker + ".attempts"));
            Assert.Equal(sourceBytes, new JournalStore(fixture.Source.Instance, new()).Read(first.JournalId).Export());
        }
        retries.Tick(DateTimeOffset.UtcNow);
        Assert.Equal(1, fixture.Base.Inbox.PendingCount);
        var second = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(operation, second.OperationId);
        Assert.Equal(2, second.Facts.Ordinal); Assert.Equal(first.Facts.RunId, second.Facts.RunId);
        Assert.Equal(EngineOutcome.Succeeded, second.Facts.Engine);
        Assert.Equal(first, new AttemptReceiptStore(fixture.Source.Database).Read(first.Facts.AttemptId));
        Assert.Single(Directory.GetFiles(fixture.Marker + ".effects"));
    }

    [Fact(DisplayName = "Cancelling An Actually Running Successor Stops Its Own Job And Cannot Erase The Earlier Failure Or Create A Third Attempt")]
    public async Task CancelsActualSuccessor()
    {
        await using var fixture = await Create("retry-second-hold");
        var first = await fixture.RunInterruptedAsync(); var retries = Retry(fixture);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        var accepted = Assert.IsType<AttemptAcceptanceResult>(retries.Start(pending, DateTimeOffset.UtcNow));
        var running = fixture.Consumer.RunNextAsync();
        AttemptReceipt? second = null;
        try
        {
            await AwaitSecondWorker(fixture.Marker);
        }
        finally
        {
            var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
            var cancellation = new ExecutionCancellationStore(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease);
            cancellation.Cancel(ExecutionCancellationStore.Identity(Guid.NewGuid(), "provider-runtime-fixture", accepted.JournalId,
                state.Journal.OperationId, Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), accepted.JournalId, DateTimeOffset.UtcNow);
            second = Assert.IsType<AttemptReceipt>(await running);
        }
        Assert.NotNull(second);
        Assert.Equal(LifecycleOutcome.Cancelled, second.Facts.Lifecycle);
        Assert.Equal(first.Facts.RunId, second.Facts.RunId); Assert.Equal(2, second.Facts.Ordinal);
        Assert.Equal(LifecycleOutcome.Interrupted, first.Facts.Lifecycle);
        Assert.Equal(first, new AttemptReceiptStore(fixture.Source.Database).Read(first.Facts.AttemptId));
        retries.Tick(DateTimeOffset.UtcNow);
        Assert.Null(retries.ReadWaiting(first.Facts.RunId)); Assert.Equal(0, fixture.Base.Inbox.PendingCount);
        Assert.Equal(2, File.ReadAllLines(fixture.Marker + ".attempts").Length);
        var stateAfter = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), second.JournalId);
        var writer = Assert.Single(stateAfter.Steps);
        Assert.NotNull(new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadForExecutionEvidence(second.JournalId, writer.WriterId, writer.JobName, writer.SessionId));
        Assert.True(new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new()).IsFinal(second));
    }

    [Theory(DisplayName = "A Real Terminal Transaction Failure Cannot Publish Only A Receipt Or Only A Retry And Blocks Any Automatic Successor")]
    [InlineData("attempt_receipts")]
    [InlineData("execution_retry_intents")]
    public async Task RequiresAtomicTerminal(string table)
    {
        await using var fixture = await Create("retry-interrupted");
        Assert.Contains(table, new[] { "attempt_receipts", "execution_retry_intents" });
        fixture.Base.Execute("CREATE TRIGGER fail_retry_terminal BEFORE INSERT ON " + table +
            " BEGIN SELECT RAISE(ABORT,'retry_terminal_fixture_fault'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.RunInterruptedAsync());
        Assert.Equal(0, fixture.Base.Count("attempt_receipts"));
        Assert.Equal(0, fixture.Base.Count("execution_retry_intents"));
        Assert.Equal(0, fixture.Base.Inbox.PendingCount);
        Assert.Equal(1, fixture.Consumer.PendingReconciliationCount);
        Assert.False(fixture.Base.Index.Current.IsReady);
        var physical = new JournalStore(fixture.Source.Instance, new()).Read(fixture.Accepted.JournalId);
        Assert.Equal(JournalPhase.Committed, physical.Phase);
        var registered = Assert.Single(new RecoveryJournalRegistry(fixture.Source.Database).ReadSnapshot().Journals,
            item => item.JournalId == fixture.Accepted.JournalId);
        Assert.Equal(JournalPhase.Applied, registered.Phase);
        Assert.Equal(registered.Revision + 1, physical.Revision);
        using (var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT status FROM operations WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$id", registered.OperationId.ToString("D"));
            Assert.Equal("accepted", command.ExecuteScalar());
        }
        Retry(fixture).Tick(DateTimeOffset.UtcNow);
        Assert.Single(File.ReadAllLines(fixture.Marker + ".attempts"));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Consumer.RunNextAsync());
        Assert.Equal(physical.Export(), new JournalStore(fixture.Source.Instance, new()).Read(fixture.Accepted.JournalId).Export());
        Assert.Equal(0, fixture.Base.Count("attempt_receipts"));
        Assert.Equal(0, fixture.Base.Count("execution_retry_intents"));
        Assert.Equal(1, fixture.Consumer.PendingReconciliationCount);
        Assert.False(fixture.Base.Index.Current.IsReady);
    }

    private static Task<ProviderRuntimeFixture> Create(string mode) => ProviderRuntimeFixture.CreateAsync(mode,
        retrySafe: true, retryApproved: true, maximumAttempts: 3);
    private static ExecutionRetryService Retry(ProviderRuntimeFixture fixture) =>
        new(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease, fixture.Service);

    private static async Task AwaitSecondWorker(string marker)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            try
            {
                if (File.Exists(marker + ".attempts") && File.ReadAllLines(marker + ".attempts").Length == 2) return;
            }
            catch (IOException) { }
            await Task.Delay(20);
        }
        throw new TimeoutException("retry_second_worker_not_observed");
    }
}
