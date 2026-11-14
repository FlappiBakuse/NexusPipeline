using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Operations;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AttemptTerminalTests
{
    [Fact(DisplayName = "A Real Collected Engine Failure Completes Without Inventing Business Success")]
    public async Task PreservesEngineFailure()
    {
        using var fixture = new StepWriterFixture("tail"); var source = fixture.Base.Source;
        var state = fixture.Scope.VerifyReadOnly();
        var binding = StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); var root = collector.Start();
        Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        await collector.ObserveExitAsync();
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: true);
        Assert.Equal(root.ProcessId, stopped.Closure.Root!.ProcessId);
        var proof = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        var terminal = Store(fixture); terminal.RecordCollected(fixture.JournalId, proof);
        terminal.FreezeDispatch(fixture.JournalId, interrupted: false); terminal.MarkApplied(fixture.JournalId);
        var receipt = terminal.Commit(fixture.JournalId, LifecycleOutcome.Completed, "execution_completed");
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        AssertTerminalProjection(fixture, state, receipt);
        var before = source.Snapshot(); Assert.Equal(receipt, terminal.Commit(fixture.JournalId, LifecycleOutcome.Completed, "execution_completed"));
        AssertSame(before, fixture);
        Assert.Throws<OperationConflictException>(() => terminal.Commit(fixture.JournalId, LifecycleOutcome.Interrupted, "host_interrupted"));
        AssertSame(before, fixture);
    }

    [Fact(DisplayName = "A Prepared Accepted Cancellation Commits Its Actual Empty Job As Cancelled Without Inventing Execution")]
    public async Task CommitsPreparedCancellation()
    {
        using var fixture = new StepWriterFixture(); var source = fixture.Base.Source;
        var original = PhysicalObjectIdentity.Read(source.Configuration); var bytes = File.ReadAllBytes(source.Configuration);
        var state = fixture.Scope.VerifyReadOnly(); var cancel = ExecutionCancellationStore.Identity(Guid.NewGuid(), "step-writer-fixture", fixture.JournalId,
            state.Journal.OperationId, Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
        new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease).Cancel(cancel, fixture.JournalId, PlanBuilderFixture.CreatedAt);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: false);
        var receipt = Store(fixture).CommitPreparedCancellation(fixture.JournalId);
        Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(RecoveryOutcome.NotRequired, receipt.Facts.Recovery);
        Assert.Equal("execution_cancel_requested", receipt.Facts.ReasonCode);
        var completed = fixture.Scope.VerifyReadOnly(); Assert.Equal(JournalPhase.Committed, completed.Journal.Phase);
        Assert.Equal(EffectGateState.Sealed, completed.Gate.State); Assert.Equal(completed.Journal.Revision, completed.Latest.Revision);
        AssertTerminalProjection(fixture, state, receipt);
        Assert.Null(fixture.ReadRoot(completed)); Assert.Empty(stopped.Closure.Family);
        Assert.False(File.Exists(fixture.Marker));
        Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration)); Assert.Equal(bytes, File.ReadAllBytes(source.Configuration));
        var before = source.Snapshot(); Assert.Equal(receipt, Store(fixture).CommitPreparedCancellation(fixture.JournalId)); AssertSame(before, fixture);
        Assert.Throws<OperationConflictException>(() => Store(fixture).Commit(fixture.JournalId, LifecycleOutcome.Completed, "execution_completed"));
        AssertSame(before, fixture);
    }

    [Theory(DisplayName = "Terminal Receipt Operation And Isolation Projection Roll Back Together Then Reuse One Physical Result")]
    [InlineData("receipt")]
    [InlineData("operation")]
    [InlineData("projection")]
    public async Task ReplaysTerminalProjection(string boundary)
    {
        using var fixture = new StepWriterFixture(); var source = fixture.Base.Source;
        var state = fixture.Scope.VerifyReadOnly(); var cancel = ExecutionCancellationStore.Identity(Guid.NewGuid(), "step-writer-fixture", fixture.JournalId,
            state.Journal.OperationId, Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
        new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease).Cancel(cancel, fixture.JournalId, PlanBuilderFixture.CreatedAt);
        fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), executed: false);
        var registry = new RecoveryJournalRegistry(source.Database);
        var held = registry.ReadSnapshot().Isolations.Where(item => item.JournalId == fixture.JournalId)
            .OrderBy(item => item.ResourceKey, StringComparer.Ordinal).ToArray();
        Assert.Equal(state.Attempt.ResourceKeys.Order(StringComparer.Ordinal), held.Select(item => item.ResourceKey));
        var trigger = boundary switch
        {
            "receipt" => "BEFORE INSERT ON attempt_receipts",
            "operation" => "BEFORE UPDATE OF status ON operations WHEN NEW.operation_id='" + state.Journal.OperationId.ToString("D") + "' AND NEW.status='completed'",
            _ => "BEFORE UPDATE OF phase ON operation_journals WHEN NEW.journal_id='" + fixture.JournalId.ToString("D") + "' AND NEW.phase='committed'"
        };
        var triggerName = (boundary == "receipt" ? "records" : "main") + ".fail_attempt_terminal";
        fixture.Base.Execute("CREATE TRIGGER " + triggerName + " " + trigger + " BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        byte[] committedBytes;
        try
        {
            Assert.Throws<SqliteException>(() => Store(fixture).CommitPreparedCancellation(fixture.JournalId));
            var pending = fixture.Scope.VerifyReadOnly(); Assert.Equal(JournalPhase.Prepared, pending.Journal.Phase);
            Assert.Equal(JournalPhase.Committed, pending.Latest.Phase); committedBytes = pending.Latest.Export();
            Assert.Null(new AttemptReceiptStore(source.Database).Read(Guid.Parse(state.Attempt.AttemptId)));
            Assert.Equal(held, registry.ReadSnapshot().Isolations.Where(item => item.JournalId == fixture.JournalId)
                .OrderBy(item => item.ResourceKey, StringComparer.Ordinal));
            AssertOperation(fixture, "accepted");
            Assert.False(File.Exists(fixture.Marker));
        }
        finally { fixture.Base.Execute("DROP TRIGGER " + triggerName + ";"); }
        var result = Store(fixture).CommitPreparedCancellation(fixture.JournalId);
        var completed = fixture.Scope.VerifyReadOnly(); Assert.Equal(JournalPhase.Committed, completed.Journal.Phase);
        Assert.Equal(completed.Latest.Revision, completed.Journal.Revision);
        Assert.Equal(committedBytes, completed.Latest.Export());
        Assert.Equal(LifecycleOutcome.Cancelled, result.Facts.Lifecycle);
        AssertTerminalProjection(fixture, state, result);
    }

    [Fact(DisplayName = "Entered Even Without Resume Cannot Use The Prepared Cancellation Shortcut")]
    public async Task RejectsEnteredCancellation()
    {
        using var fixture = new StepWriterFixture(); var source = fixture.Base.Source; fixture.Scope.EnterApplying();
        var state = fixture.Scope.VerifyReadOnly(); var cancel = ExecutionCancellationStore.Identity(Guid.NewGuid(), "step-writer-fixture", fixture.JournalId,
            state.Journal.OperationId, Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
        new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease).Cancel(cancel, fixture.JournalId, PlanBuilderFixture.CreatedAt);
        fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), executed: false);
        var before = source.Snapshot(); Assert.Throws<IOException>(() => Store(fixture).CommitPreparedCancellation(fixture.JournalId)); AssertSame(before, fixture);
        Assert.Equal(EffectGateState.Entered, fixture.Scope.VerifyReadOnly().Gate.State);
        Assert.Null(new AttemptReceiptStore(source.Database).Read(Guid.Parse(state.Attempt.AttemptId)));
    }

    private static AttemptTerminalStore Store(StepWriterFixture fixture) =>
        new(fixture.Base.Source.Instance, fixture.Base.Source.Database, new(), fixture.Base.Source.Lease);
    private static void AssertSame((string[] Rows, string[] Files) before, StepWriterFixture fixture)
    {
        var after = fixture.Base.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }
    private static void AssertTerminalProjection(StepWriterFixture fixture, AttemptWriterState state, AttemptReceipt receipt)
    {
        var database = fixture.Base.Source.Database;
        Assert.Equal(fixture.JournalId, receipt.JournalId);
        Assert.Equal(fixture.Accepted.Identity.OperationId, receipt.OperationId);
        Assert.Equal(Guid.Parse(state.Attempt.AttemptId), receipt.Facts.AttemptId);
        var history = new AttemptHistoryReader(database).ReadPage(new(Guid.Parse(state.Attempt.RunId)));
        Assert.Equal(receipt, Assert.Single(history.Items));
        AssertOperation(fixture, "completed");
        Assert.DoesNotContain(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations, item => item.JournalId == fixture.JournalId);
    }

    private static void AssertOperation(StepWriterFixture fixture, string status)
    {
        var source = fixture.Base.Source;
        var identity = fixture.Accepted.Identity;
        var operations = new OperationQueryService(source.Instance, source.Lease, source.Database);
        Assert.Equal(new OperationSnapshot(identity.OperationId, identity.Kind, status, fixture.JournalId),
            operations.Read(identity.OperationId, identity.SubjectId));
    }
}
