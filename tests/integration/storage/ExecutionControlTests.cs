using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Operations;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExecutionControlTests
{
    [Theory(DisplayName = "Subject Bound Control Cancellation Replays Original Dispatch Or Waiting Decline Without Inventing Writer Stop")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindsCancellationSubjects(bool waiting)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync(waiting ? "retry-interrupted" : "wait",
            retrySafe: waiting, retryApproved: waiting, maximumAttempts: waiting ? 2 : 1);
        var retries = new ExecutionRetryService(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease, fixture.Service);
        var control = new ExecutionControlService(fixture.Source.Instance, fixture.Source.Database, new(),
            fixture.Source.Lease, fixture.Service, retries);
        var running = waiting ? fixture.RunInterruptedAsync() : fixture.RunAsync();
        if (waiting) _ = await running; else await fixture.ReadyAsync();
        var state = fixture.State;
        var run = Guid.Parse(state.Attempt.RunId); var attempt = Guid.Parse(state.Attempt.AttemptId);
        var operation = Guid.NewGuid(); var counts = fixture.Base.Counts();
        Assert.ThrowsAny<Exception>(() => control.Cancel(operation, "foreign", state.Journal.OperationId, run, attempt, DateTimeOffset.UtcNow));
        Assert.Equal(counts, fixture.Base.Counts());
        var originalReceipt = new AttemptReceiptStore(fixture.Source.Database).Read(attempt);
        var accepted = control.Cancel(operation, "provider-runtime-fixture", state.Journal.OperationId, run, attempt, DateTimeOffset.UtcNow);
        Assert.Equal(waiting ? "retry-declined" : "dispatch-closed", accepted.Acknowledgement);
        var replay = control.Cancel(operation, "provider-runtime-fixture", state.Journal.OperationId, run, attempt, DateTimeOffset.UtcNow);
        Assert.False(replay.Acceptance.IsNew);
        Assert.Equal(accepted with { Acceptance = replay.Acceptance }, replay);
        var query = new OperationQueryService(fixture.Source.Instance, fixture.Source.Lease, fixture.Source.Database);
        Assert.Equal("completed", query.Read(operation, "provider-runtime-fixture")!.Status);
        Assert.Null(query.Read(operation, "foreign"));
        var receipt = await running;
        if (waiting)
        {
            Assert.Equal(originalReceipt, receipt);
            Assert.Equal("retry_run_cancelled", new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new())
                .ReadDecision(receipt).DeclineReasonCode);
            Assert.Null(retries.ReadWaiting(run));
            ExecutionRetryCancellation.VerifyInventory(fixture.Source.Database);
        }
        else
        {
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            var step = Assert.Single(fixture.State.Steps);
            var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
                .ReadForExecutionEvidence(state.Journal.JournalId, step.WriterId, step.JobName, step.SessionId);
            Assert.NotNull(closure); Assert.True(closure.WasExecuted);
            Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        }
    }

    [Theory(DisplayName = "A Caller Cancellation Operation And Original Waiting Decline Share One Actual Sql Transaction")]
    [InlineData("ABORT")]
    [InlineData("IGNORE")]
    public async Task SharesDeclineTransaction(string failure)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var receipt = await fixture.RunInterruptedAsync();
        var retries = new ExecutionRetryService(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease, fixture.Service);
        var control = new ExecutionControlService(fixture.Source.Instance, fixture.Source.Database, new(),
            fixture.Source.Lease, fixture.Service, retries);
        var operation = Guid.NewGuid(); var counts = fixture.Base.Counts();
        fixture.Base.Execute("CREATE TRIGGER main.control_cancel_failure BEFORE UPDATE OF status ON operations " +
            "WHEN NEW.operation_kind='execution.retry.cancel' BEGIN SELECT RAISE(" + failure +
            (failure == "ABORT" ? ",'fixture_failure'" : "") + "); END;");
        try
        {
            Assert.ThrowsAny<Exception>(() => control.Cancel(operation, "provider-runtime-fixture", receipt.OperationId,
                receipt.Facts.RunId, receipt.Facts.AttemptId, DateTimeOffset.UtcNow));
            Assert.Equal(counts, fixture.Base.Counts());
            Assert.Equal(0, fixture.Base.Count("execution_retry_outcomes"));
            Assert.False(new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new()).IsFinal(receipt));
            Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        }
        finally { fixture.Base.Execute("DROP TRIGGER main.control_cancel_failure;"); }
        var result = control.Cancel(operation, "provider-runtime-fixture", receipt.OperationId, receipt.Facts.RunId,
            receipt.Facts.AttemptId, DateTimeOffset.UtcNow);
        Assert.Equal("retry-declined", result.Acknowledgement);
        Assert.Single(File.ReadAllLines(fixture.Marker + ".attempts"));
    }
}
