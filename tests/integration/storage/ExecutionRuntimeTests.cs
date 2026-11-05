using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExecutionRuntimeTests
{
    [Theory(DisplayName = "Actual Inbox Consumption Completes Every Native Step And Restores Configuration Before Releasing Ownership")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletesOwnedSteps(bool configuration)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration); var source = fixture.Source;
        var bytes = File.ReadAllBytes(source.Configuration); var original = PhysicalObjectIdentity.Read(source.Configuration);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(JournalPhase.Committed, fixture.State.Journal.Phase);
        Assert.Equal(configuration ? 6 : 3, fixture.Base.Count("journal_writer_job_roots")); Assert.Equal(3, fixture.State.Coverage.Length);
        Assert.All(fixture.State.Coverage, item => Assert.Equal(StepCompletionKind.Collected, item.Kind));
        var reader = new StepEvidenceReader(source.Instance, source.Database, new());
        foreach (var result in StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total))
        {
            _ = reader.Read(result.Binding, result.Latest.Artifact, collected: true);
            var evidence = StepEvidenceCodec.Read(new ImmutableBlobStore(source.Instance, new()), result.Binding, result.Latest.Artifact);
            Assert.Equal(37, evidence.ExitCode); Assert.NotNull(evidence.Root);
            var manifest = RawOutputCodec.Read(new ImmutableBlobStore(source.Instance, new()), result.Binding, evidence.RawOutput!);
            Assert.NotNull(manifest.StandardOutputEofOffset); Assert.NotNull(manifest.StandardErrorEofOffset); Assert.True(manifest.IsComplete);
        }
        Assert.Equal(bytes, File.ReadAllBytes(source.Configuration)); Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.Equal(1, fixture.Base.Count("attempt_receipts")); Assert.Equal(0, fixture.Base.Count("resource_isolations"));
        Assert.True(fixture.Base.Index.Current.IsReady); Assert.Equal(0, fixture.Base.Inbox.PendingCount);
        Assert.Null(await fixture.Consumer.RunNextAsync()); Assert.Null(fixture.Consumer.FailureCode);
        var capacity = JournalCapacityPolicy.Measure(fixture.State.Total);
        var history = new JournalStore(source.Instance, new()).ReadAllRevisions(fixture.Accepted.JournalId);
        Assert.True(history.Count <= capacity.Revisions); Assert.True(history.Sum(item => item.Export().LongLength) <= capacity.HistoryBytes);
        Assert.All(history, item => Assert.True(item.Export().LongLength <= capacity.MaximumDocumentBytes));
        var next = fixture.Base.Service.Start(Guid.NewGuid(), ExecutionSequentialFixture.Subject,
            source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        Assert.True(next.Acceptance.IsNew); Assert.Equal(1, fixture.Base.Inbox.PendingCount);
    }

    [Theory(DisplayName = "Accepted Cancellation Before Consumption Creates No Native Job And Closes The Whole Suffix")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelsUnstartedSuffix(bool configuration)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration, mode: "wait");
        var bytes = File.ReadAllBytes(fixture.Source.Configuration); fixture.Cancel();
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        Assert.Equal(RecoveryOutcome.NotRequired, receipt.Facts.Recovery); Assert.Equal(EffectGateState.Sealed, fixture.State.Gate.State);
        Assert.Equal(0, fixture.Base.Count("journal_writer_jobs")); Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
        Assert.All(fixture.State.Coverage, item => { Assert.Equal(StepCompletionKind.NotDispatched, item.Kind); Assert.Null(item.Result); });
        Assert.False(File.Exists(fixture.Marker)); Assert.Equal(bytes, File.ReadAllBytes(fixture.Source.Configuration));
        Assert.Equal(0, fixture.Consumer.PendingReconciliationCount); Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Theory(DisplayName = "Cancellation Stops The Real Root And Inherited Output Child Without Dispatching Successors")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopsInheritedWriters(bool configuration)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration, mode: "child-wait");
        var running = fixture.Consumer.RunNextAsync();
        await fixture.Ready(".child.output-ready");
        var views = fixture.Consumer.ReadActive(ExecutionSequentialFixture.Subject);
        var active = Assert.Single(views);
        fixture.Consumer.ObserveCancellation(active.OperationId, active.RunId, Guid.NewGuid());
        Assert.False(Assert.Single(fixture.Consumer.ReadActive(ExecutionSequentialFixture.Subject)).CancellationRequested);
        fixture.Cancel();
        fixture.Consumer.ObserveCancellation(active.OperationId, active.RunId, active.AttemptId);
        var stopping = Assert.Single(fixture.Consumer.ReadActive(ExecutionSequentialFixture.Subject));
        Assert.True(stopping.CancellationRequested); Assert.False(stopping.CanCancel);
        Assert.Equal("stopping", stopping.Phase);
        var receipt = Assert.IsType<AttemptReceipt>(await running);
        Assert.All(active.Steps, step => Assert.Equal(new[] { "main" }, step.LogRoles));
        var display = Assert.IsType<ExecutionRunDisplay>(active.Display);
        Assert.Equal("queue", display.TargetKind); Assert.Equal(fixture.Source.QueueId, display.TargetId);
        Assert.Equal(fixture.Source.Definitions.Read().Queues.Single().Name, display.TargetName);
        Assert.Equal("manual", display.Mode); Assert.Equal(fixture.State.Total.RetryPolicy.MaximumAttempts, display.MaximumAttempts);
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Single(StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        Assert.Equal(configuration ? 2 : 1, fixture.Base.Count("journal_writer_job_roots"));
        var writer = Assert.Single(fixture.State.Steps);
        var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadForExecutionEvidence(fixture.Accepted.JournalId, writer.WriterId, writer.JobName, writer.SessionId);
        Assert.NotNull(closure); Assert.True(closure.Family.Length >= 2);
        Assert.All(closure.Family, process => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(process)));
        Assert.All(fixture.State.Coverage.Skip(1), item => Assert.Equal(StepCompletionKind.NotDispatched, item.Kind));
        Assert.True(fixture.Base.Index.Current.IsReady); Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
    }

    [Theory(DisplayName = "The Shared Expired Budget Stops Before Any Native Job And Still Completes Prepared Configuration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsExpiredBudget(bool configuration)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration, expired: true);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Blocked, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        Assert.Equal("execution_total_budget_exhausted", receipt.Facts.ReasonCode);
        Assert.Equal(0, fixture.Base.Count("journal_writer_jobs")); Assert.False(File.Exists(fixture.Marker));
        Assert.All(fixture.State.Coverage, item => Assert.Equal(StepCompletionKind.NotDispatched, item.Kind));
        Assert.True(fixture.Base.Index.Current.IsReady); Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }

    [Fact(DisplayName = "An Expired Binding Budget Stops Its Actual Writer And Prevents Ordinary Retry While The Run Budget Remains Live")]
    public async Task EnforcesBindingBudget()
    {
        using var fixture = new ExecutionSequentialFixture(mode: "wait", accept: false,
            limits: new(3, 0, 120));
        var source = fixture.Source; var saved = source.Definitions.Read();
        var binding = saved.Bindings.Single(item => item.ScriptId == saved.Queues.Single().ScriptIds[0] && item.UserId == source.UserA).Id;
        var operation = Guid.NewGuid();
        fixture.Base.Service.Start(operation, ExecutionSequentialFixture.Subject, source.Request(new BindingTarget(binding)), DateTimeOffset.UtcNow);
        using var accepted = fixture.Base.Take(operation); var parent = accepted.Receipt.JournalId;
        var input = Assert.Single(accepted.Prepared.Total.Plan.ReadWire().Steps);
        long elapsed = 0;
        using var step = new ExecutionRuntimeStep(source.Instance, source.Database, new(), source.Lease, fixture.Base.Index,
            accepted, input, null, fixture.Base.Resources, bindingElapsed: () => TimeSpan.FromMilliseconds(Interlocked.Read(ref elapsed)));
        step.InitializeCapture(); step.EnterEffect();
        using var stopping = new CancellationTokenSource();
        var running = step.ExecuteAsync(() => null, stopping.Token);
        try
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(fixture.Marker + ".output-ready") && !running.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(15))
                await Task.Delay(20);
            Assert.True(File.Exists(fixture.Marker + ".output-ready"));
            Interlocked.Exchange(ref elapsed, 120000);
            Assert.Null(await running.WaitAsync(TimeSpan.FromSeconds(30)));
            await step.CollectAsync(); await step.RestoreAsync(interrupted: false);
            var state = AttemptWriterState.Read(source.Instance, source.Database, new(), parent);
            var timeout = Assert.Single(BindingTimeoutCodec.Read(state.Latest.Root, state.Total));
            Assert.Equal(120000, timeout.ConsumedMilliseconds); Assert.Equal(input.StepId, timeout.StepId);
            Assert.True(accepted.Prepared.TimeBudget.Read().Remaining > TimeSpan.Zero);
            Assert.False(ExecutionInvocationStore.CanRetry(state, input.StepId)); Assert.Empty(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total));
            var job = Assert.Single(state.AllJobs);
            var closure = new JobClosureStore(source.Instance, source.Database, new())
                .ReadForExecutionEvidence(parent, job.WriterId, job.JobName, job.SessionId);
            Assert.NotNull(closure); Assert.True(closure.WasExecuted); Assert.NotEmpty(closure.Family);
            Assert.All(closure.Family, process => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(process)));
            var terminal = new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease);
            terminal.FreezeDispatch(parent, interrupted: false); terminal.CloseUndispatched(parent); terminal.MarkApplied(parent);
            using var budget = source.Lease.RunBudgets.PrepareTerminal(source.Database, accepted, fixture.Base.Resources, TimeProvider.System);
            var receipt = terminal.Commit(parent, LifecycleOutcome.Completed, "execution_completed", budget);
            Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(0, fixture.Base.Count("resource_isolations"));
        }
        finally
        {
            stopping.Cancel();
            if (!running.IsCompleted) await running.WaitAsync(TimeSpan.FromSeconds(30));
            if (!step.IsCollected) await step.CollectAsync();
            if (!step.IsRestored) await step.RestoreAsync(interrupted: true);
        }
    }

    [Fact(DisplayName = "Host Shutdown Drains Accepted Work As Interrupted Without Starting A Process")]
    public async Task DrainsInterruptedWork()
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: true);
        using var stopping = new CancellationTokenSource(); stopping.Cancel(); await fixture.Consumer.RunAsync(stopping.Token);
        var receipt = new AttemptReceiptStore(fixture.Source.Database).Read(Guid.Parse(fixture.State.Attempt.AttemptId))!;
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        Assert.Equal(0, fixture.Base.Count("journal_writer_jobs")); Assert.False(File.Exists(fixture.Marker));
        Assert.Equal(0, fixture.Base.Inbox.PendingCount); Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Fact(DisplayName = "A Waiting Consumer Rejects A Second Consumer And Wakes Only For An Accepted Submission")]
    public async Task OwnsSingleConsumer()
    {
        using var fixture = new ExecutionRuntimeFixture(mode: "wait");
        fixture.Cancel(); _ = await fixture.Consumer.RunNextAsync();
        using var stopping = new CancellationTokenSource(); var running = fixture.Consumer.RunAsync(stopping.Token);
        var rejected = await Assert.ThrowsAsync<IOException>(() => fixture.Consumer.RunNextAsync());
        Assert.Equal("execution_runtime_consumer_already_active", rejected.Message);
        _ = fixture.Base.Service.Start(Guid.NewGuid(), ExecutionSequentialFixture.Subject,
            fixture.Source.Request(new BindingTarget(fixture.Source.OtherBinding)), DateTimeOffset.UtcNow);
        await fixture.Ready(".output-ready"); stopping.Cancel(); await running;
        Assert.Equal(2, fixture.Base.Count("attempt_receipts")); Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
        Assert.Null(await fixture.Consumer.RunNextAsync()); Assert.True(fixture.Base.Index.Current.IsReady);
    }
}
