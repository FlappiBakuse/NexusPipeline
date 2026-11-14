using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExecutionSequentialTests
{
    [Fact(DisplayName = "Jobs Follow The Frozen Queue Order And Wait For The Previous Actual Family And Output Collection")]
    public async Task PreservesQueueOrder()
    {
        using var fixture = new ExecutionSequentialFixture();
        Assert.Throws<IOException>(() => fixture.Scope(1)); Assert.Equal(0, fixture.Base.Count("journal_writer_jobs"));
        var first = fixture.Create(0); Assert.Equal(1, fixture.Base.Count("journal_writer_jobs")); first.Start(); await first.Exit(); await first.Stop();
        Assert.Throws<IOException>(() => fixture.Scope(1)); Assert.Equal(1, fixture.Base.Count("journal_writer_jobs"));
        await first.Collect(); Assert.Throws<IOException>(() => fixture.Scope(2));
        await fixture.Create(1).Complete(); Assert.Equal(2, fixture.Base.Count("journal_writer_jobs"));
        await fixture.Create(2).Complete(); Assert.Equal(3, fixture.Base.Count("journal_writer_jobs"));
        Assert.Equal(fixture.Steps.Select(step => step.StepId), fixture.State.Steps.Select(step => step.StepId));
        fixture.Terminal.FreezeDispatch(fixture.ParentId, interrupted: false); fixture.Terminal.CloseUndispatched(fixture.ParentId);
        fixture.Terminal.MarkApplied(fixture.ParentId);
        var receipt = fixture.Terminal.Commit(fixture.ParentId, LifecycleOutcome.Completed, "execution_completed");
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.All(fixture.State.Coverage, item => Assert.Equal(StepCompletionKind.Collected, item.Kind));
        Assert.Equal(3, fixture.Base.Count("journal_writer_job_roots")); Assert.Equal(3, fixture.Base.Count("journal_writer_job_closures"));
    }

    [Fact(DisplayName = "Physical Restoration Without The Predecessor Final Group Transaction Cannot Authorize A Successor")]
    public async Task RequiresCompletedPredecessors()
    {
        using var fixture = new ExecutionSequentialFixture(configuration: true);
        var original = PhysicalObjectIdentity.Read(fixture.Source.Configuration); var originalBytes = File.ReadAllBytes(fixture.Source.Configuration);
        var first = fixture.Create(0); first.Start(); await first.Exit(); await first.Stop(); await first.Collect();
        Assert.Throws<IOException>(() => fixture.Scope(1));
        var sessionId = fixture.Sessions[fixture.Steps[0].StepId]; var completion = Guid.NewGuid();
        fixture.Base.Execute("CREATE TRIGGER configuration.fixture_predecessor_failure BEFORE UPDATE OF phase ON configuration_run_sessions " +
            $"WHEN NEW.session_id='{sessionId:D}' AND NEW.phase=2 BEGIN SELECT RAISE(ABORT,'fixture_predecessor_failure'); END;");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => first.FinishConfiguration(completion));
            Assert.Equal(original, PhysicalObjectIdentity.Read(fixture.Source.Configuration)); Assert.Equal(originalBytes, File.ReadAllBytes(fixture.Source.Configuration));
            var runs = new ConfigurationRunStore(fixture.Source.Instance, fixture.Source.Database, new());
            Assert.Equal(ConfigurationRunPhase.Running, runs.Read(sessionId, ExecutionSequentialFixture.Subject)!.Phase);
            Assert.Throws<IOException>(() => fixture.Scope(1)); Assert.Equal(2, fixture.Base.Count("journal_writer_jobs"));
        }
        finally { fixture.Base.Execute("DROP TRIGGER configuration.fixture_predecessor_failure;"); }
        await first.FinishConfiguration(completion);
        await fixture.Create(1).Complete(); await fixture.Create(2).Complete();
        Assert.Equal(original, PhysicalObjectIdentity.Read(fixture.Source.Configuration)); Assert.Equal(originalBytes, File.ReadAllBytes(fixture.Source.Configuration));
    }

    [Theory(DisplayName = "Cancellation Keeps The Registered Job Distinct From The Uncreated Not Started Suffix")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsRegisteredSuffix(bool unstarted)
    {
        using var fixture = new ExecutionSequentialFixture(mode: "wait"); var first = fixture.Create(0);
        if (unstarted) first.Scope.EnterApplying();
        else { first.Start(); await Ready(fixture.Marker + ".output-ready"); }
        Cancel(fixture); fixture.Terminal.CloseUndispatched(fixture.ParentId); await first.Stop(); await first.Collect();
        fixture.Terminal.MarkApplied(fixture.ParentId);
        var receipt = fixture.Terminal.Commit(fixture.ParentId, LifecycleOutcome.Cancelled, "execution_cancel_requested");
        Assert.Equal(unstarted ? EngineOutcome.NotStarted : EngineOutcome.Unknown, receipt.Facts.Engine);
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(1, fixture.Base.Count("journal_writer_jobs")); Assert.Equal(unstarted ? 0 : 1, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(1, fixture.Base.Count("journal_writer_job_closures")); Assert.Single(StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        Assert.All(fixture.State.Coverage.Skip(1), item => { Assert.Equal(StepCompletionKind.NotDispatched, item.Kind); Assert.Null(item.Result); });
        Assert.Throws<IOException>(() => fixture.Scope(1));
    }

    [Theory(DisplayName = "Successor Job Registration And Accepted Cancellation Keep Their Actual Winning Order")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdersSuccessorCancellation(bool cancelFirst)
    {
        using var fixture = new ExecutionSequentialFixture(); await fixture.Create(0).Complete();
        var scope = fixture.Scope(1); var source = fixture.Source;
        if (cancelFirst)
        {
            Cancel(fixture); var before = source.Snapshot();
            Assert.Throws<IOException>(() => new ConfigurationWriterProcess(source.Instance, source.Lease, scope));
            var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
            Assert.Equal(1, fixture.Base.Count("journal_writer_jobs"));
        }
        else
        {
            using var writer = new ConfigurationWriterProcess(source.Instance, source.Lease, scope);
            var state = fixture.State; var job = state.Steps[1];
            var binding = StepResultCodec.Binding(state.Latest.Root, job);
            using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, writer);
            Cancel(fixture);
            Assert.Throws<IOException>(() => collector.Start());
            var stopped = await writer.StopAsync(TimeSpan.FromSeconds(10));
            var proof = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
            fixture.Terminal.RecordCollected(fixture.ParentId, proof);
            Assert.Equal(2, fixture.Base.Count("journal_writer_jobs")); Assert.Equal(1, fixture.Base.Count("journal_writer_job_roots"));
            Assert.Equal(2, fixture.Base.Count("journal_writer_job_closures"));
        }
        fixture.Terminal.CloseUndispatched(fixture.ParentId); fixture.Terminal.MarkApplied(fixture.ParentId);
        var receipt = fixture.Terminal.Commit(fixture.ParentId, LifecycleOutcome.Cancelled, DispatchIntentCodec.Reason);
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Equal(cancelFirst ? 2 : 1, fixture.State.Coverage.Count(item => item.Kind == StepCompletionKind.NotDispatched));
    }

    [Theory(DisplayName = "A Host Or Budget Stop Before Effect Entry Uses Real No Effect Evidence Without Inventing A Job")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProvesUnstartedStops(bool budget, bool actualEmptyJob)
    {
        using var fixture = new ExecutionSequentialFixture();
        if (actualEmptyJob) { var first = fixture.Create(0); await first.Stop(); await first.Collect(record: false); }
        var terminal = budget ? LifecycleOutcome.Blocked : LifecycleOutcome.Interrupted;
        var reason = budget ? DispatchIntentCodec.BudgetReason : DispatchIntentCodec.InterruptionReason;
        fixture.Terminal.PrepareStopped(fixture.ParentId, terminal, reason);
        Assert.Equal(EffectGateState.Sealed, fixture.State.Gate.State); Assert.Equal(JournalPhase.Prepared, fixture.State.Journal.Phase);
        Assert.Equal(actualEmptyJob ? 1 : 0, fixture.Base.Count("journal_writer_jobs")); Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
        var receipt = fixture.Terminal.CommitPreparedStop(fixture.ParentId, terminal, reason);
        Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(terminal, receipt.Facts.Lifecycle); Assert.Equal(RecoveryOutcome.NotRequired, receipt.Facts.Recovery);
        Assert.Equal(receipt, fixture.Terminal.CommitPreparedStop(fixture.ParentId, terminal, reason));
        Assert.Equal(1, fixture.Base.Count("attempt_receipts")); Assert.Equal(JournalPhase.Committed, fixture.State.Journal.Phase);
    }

    [Theory(DisplayName = "An Entered Attempt Cannot Use The Prepared Host Or Budget No Effect Shortcut")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsEnteredShortcuts(bool budget)
    {
        using var fixture = new ExecutionSequentialFixture(); var first = fixture.Create(0); first.Scope.EnterApplying(); await first.Stop(); await first.Collect();
        var before = fixture.Source.Snapshot();
        Assert.Throws<IOException>(() => fixture.Terminal.PrepareStopped(fixture.ParentId, budget ? LifecycleOutcome.Blocked : LifecycleOutcome.Interrupted,
            budget ? DispatchIntentCodec.BudgetReason : DispatchIntentCodec.InterruptionReason));
        var after = fixture.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.Equal(EffectGateState.Entered, fixture.State.Gate.State); Assert.Equal(0, fixture.Base.Count("attempt_receipts"));
    }

    [Fact(DisplayName = "Recovery Preserves A Real Collected Failure And Never Dispatches The Missing Successors")]
    public async Task KeepsCollectedFailure()
    {
        using var fixture = new ExecutionSequentialFixture(); await fixture.Create(0).Complete();
        var result = new EnteredAttemptRecovery(new EnteredRecoverySession(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease,
            fixture.ParentId, () =>
            {
                var observed = new JournalWriterStore(fixture.Source.Database).ObserveForCurrentExecutor(fixture.ParentId);
                return new(fixture.Source.Instance.InstanceId, fixture.ParentId, observed.IsComplete, observed.State);
            })).Recover();
        Assert.Equal(JournalRecoveryResult.Restored, result); Assert.Equal(JournalPhase.Committed, fixture.State.Journal.Phase);
        Assert.Equal(1, fixture.Base.Count("journal_writer_jobs"));
        var receipt = new AttemptReceiptStore(fixture.Source.Database).Read(Guid.Parse(fixture.State.Attempt.AttemptId))!;
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
        Assert.All(fixture.State.Coverage.Skip(1), item => Assert.Equal(StepCompletionKind.NotDispatched, item.Kind));
    }

    [Theory(DisplayName = "Damaged Not Dispatched Facts Are Rejected Read Only Without Opening A New Job")]
    [InlineData("order")]
    [InlineData("close")]
    [InlineData("extra")]
    public async Task RejectsDamagedSuppression(string field)
    {
        using var fixture = new ExecutionSequentialFixture(); await fixture.Create(0).Complete(); Cancel(fixture); fixture.Terminal.CloseUndispatched(fixture.ParentId);
        var state = fixture.State; var path = Path.Combine(fixture.Source.Instance.RuntimeRoot, "journals", fixture.ParentId.ToString("D"), state.Latest.Revision.ToString("D10") + ".json");
        var original = File.ReadAllBytes(path); var node = JsonNode.Parse(original)!.AsObject();
        var item = node["participants"]!.AsArray().Single(participant => participant!["identity"]!["format"]!.GetValue<string>() == StepResultCodec.UndispatchedFormat)!["identity"]!;
        if (field == "order") item["stepIds"] = new JsonArray(fixture.Steps.Skip(1).Reverse().Select(step => JsonValue.Create(step.StepId)).ToArray());
        else if (field == "close") item["dispatchOperationId"] = Guid.NewGuid().ToString("D");
        else item["stepIds"]!.AsArray().Add("not-a-frozen-step");
        File.WriteAllText(path, node.ToJsonString()); var before = fixture.Source.Snapshot();
        try
        {
            Assert.Throws<JsonException>(() => { _ = fixture.State; }); Assert.Equal(1, fixture.Base.Count("journal_writer_jobs"));
            var after = fixture.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        }
        finally { File.WriteAllBytes(path, original); }
    }

    private static void Cancel(ExecutionSequentialFixture fixture)
    {
        var state = fixture.State;
        new ExecutionCancellationStore(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease).Cancel(
            ExecutionCancellationStore.Identity(Guid.NewGuid(), ExecutionSequentialFixture.Subject, fixture.ParentId, state.Journal.OperationId,
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), fixture.ParentId, PlanBuilderFixture.CreatedAt);
    }
    private static async Task Ready(string marker)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(marker) && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(File.Exists(marker));
    }
}
