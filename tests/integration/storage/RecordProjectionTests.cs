using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RecordProjectionTests
{
    [Theory(DisplayName = "Actual Consumption Publishes One Frozen Queue Record And Exact Replay Cannot Replace Any Fact")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishesFrozenRecord(bool cancel)
    {
        using var fixture = new ExecutionRuntimeFixture();
        if (cancel) fixture.Cancel();
        var source = fixture.Source;
        var projection = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new()));
        var consumer = new ExecutionRuntimeConsumer(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index, fixture.Base.Inbox, projection);
        Assert.Empty(new RunRecordStore(source.Database).ReadPage(new()).Items);
        var receipt = Assert.IsType<AttemptReceipt>(await consumer.RunNextAsync());
        var records = new RunRecordStore(source.Database);
        var row = Assert.Single(records.ReadPage(new()).Items);
        Assert.Equal(source.QueueId, row.AutomationId); Assert.Null(row.UserId);
        Assert.Equal(receipt.Facts, row.FinalAttempt);
        Assert.Equal(cancel ? LifecycleOutcome.Cancelled : LifecycleOutcome.Completed, row.FinalAttempt.Lifecycle);
        Assert.Equal(RecordDurationKind.Measured, row.DurationKind);
        Assert.Equal(EndTimeKind.MeasuredTerminal, row.EndTimeKind);
        Assert.InRange(Assert.IsType<long>(row.DurationMilliseconds), 0, fixture.State.Total.Budget.TotalTimeoutSeconds * 1000);
        projection.OnCommitted(receipt);
        Assert.Equal(row, Assert.Single(records.ReadPage(new()).Items));
        Assert.Throws<IOException>(() => projection.OnCommitted(receipt with
        {
            Facts = receipt.Facts with { ReasonCode = "forged_terminal_reason" }
        }));
        Assert.Equal(row, Assert.Single(records.ReadPage(new()).Items));
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
    }

    [Theory(DisplayName = "A Real Waiting Retry Has No Final Record Until Its Actual Successor Or Permanent Decline")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefersFinalRecord(bool decline)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var source = fixture.Source;
        var projection = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new()));
        var first = await fixture.RunInterruptedAsync();
        projection.OnCommitted(first);
        var records = new RunRecordStore(source.Database);
        Assert.Empty(records.ReadPage(new()).Items);
        var retry = new ExecutionRetryService(source.Instance, source.Database, new(), source.Lease, fixture.Service, projection);
        AttemptReceipt terminal;
        if (decline)
        {
            retry.CancelWaiting(first.Facts.RunId, "provider-runtime-fixture", DateTimeOffset.UtcNow);
            terminal = first;
        }
        else
        {
            using var pending = Assert.IsType<PreparedExecutionRetry>(retry.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
            _ = retry.Start(pending, DateTimeOffset.UtcNow);
            var consumer = new ExecutionRuntimeConsumer(source.Instance, source.Database, new(), source.Lease,
                fixture.Base.Resources, fixture.Base.Index, fixture.Base.Inbox, projection);
            terminal = Assert.IsType<AttemptReceipt>(await consumer.RunNextAsync());
        }
        var row = Assert.Single(records.ReadPage(new()).Items);
        Assert.Equal(source.OtherScriptId, row.AutomationId);
        Assert.Equal(terminal.Facts, row.FinalAttempt);
        Assert.Equal(decline ? 1 : 2, row.FinalAttempt.Ordinal);
        Assert.Equal(decline ? RecordDurationKind.CheckpointLowerBound : RecordDurationKind.Measured, row.DurationKind);
        Assert.Equal(decline ? EndTimeKind.DeclinedRetryObservation : EndTimeKind.MeasuredTerminal, row.EndTimeKind);
        Assert.Equal(first, new AttemptReceiptStore(source.Database).Read(first.Facts.AttemptId));
        Assert.Equal(EngineOutcome.Unknown, first.Facts.Engine);
        Assert.Equal(LifecycleOutcome.Interrupted, first.Facts.Lifecycle);
        projection.OnCommitted(terminal);
        Assert.Equal(row, Assert.Single(records.ReadPage(new()).Items));
    }

    [Fact(DisplayName = "A Real Projection Sql Failure Preserves The Committed Attempt And Reconciliation Never Reruns Its Workers")]
    public async Task ReconcilesFailedProjection()
    {
        using var fixture = new ExecutionRuntimeFixture();
        var source = fixture.Source;
        var projection = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new()));
        fixture.Base.Execute("CREATE TRIGGER reject_record BEFORE INSERT ON run_records BEGIN SELECT RAISE(ABORT,'fixture_record_reject'); END;");
        var consumer = new ExecutionRuntimeConsumer(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index, fixture.Base.Inbox, projection);
        await Assert.ThrowsAsync<SqliteException>(() => consumer.RunNextAsync());
        var state = fixture.State;
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(source.Database).Read(Guid.Parse(state.Attempt.AttemptId)));
        var history = new AttemptHistoryReader(source.Database);
        var records = new RunRecordStore(source.Database);
        Assert.Equal(receipt, Assert.Single(history.ReadPage(new(receipt.Facts.RunId)).Items));
        Assert.Empty(records.ReadPage(new()).Items);
        Assert.Equal(0, fixture.Base.Inbox.PendingCount); Assert.False(fixture.Base.Index.Current.IsReady);
        var worker = File.ReadAllText(fixture.Marker);
        var journal = state.Latest.Export();
        fixture.Base.Execute("DROP TRIGGER reject_record;");
        projection.OnCommitted(receipt);
        var row = Assert.Single(records.ReadPage(new()).Items);
        Assert.Equal(source.QueueId, row.AutomationId);
        Assert.Equal(receipt.Facts, row.FinalAttempt);
        Assert.Equal(worker, File.ReadAllText(fixture.Marker));
        Assert.Equal(journal, fixture.State.Latest.Export());
        Assert.Equal(receipt, Assert.Single(history.ReadPage(new(receipt.Facts.RunId)).Items));
        Assert.Equal(0, fixture.Base.Inbox.PendingCount);
    }
}
