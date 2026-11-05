using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
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
        Assert.Equal(1, fixture.Base.Count("attempt_receipts")); Assert.Equal(0, fixture.Base.Count("resource_isolations"));
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
        Assert.Equal(1, fixture.Base.Count("attempt_receipts")); Assert.Equal(0, fixture.Base.Count("resource_isolations"));
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots")); Assert.Empty(stopped.Closure.Family);
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
        var trigger = boundary switch
        {
            "receipt" => "BEFORE INSERT ON attempt_receipts",
            "operation" => "BEFORE UPDATE OF status ON operations WHEN NEW.operation_id='" + state.Journal.OperationId.ToString("D") + "' AND NEW.status='completed'",
            _ => "BEFORE UPDATE OF phase ON operation_journals WHEN NEW.journal_id='" + fixture.JournalId.ToString("D") + "' AND NEW.phase='committed'"
        };
        fixture.Base.Execute("CREATE TRIGGER fail_attempt_terminal " + trigger + " BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        long revision; Guid resultId;
        try
        {
            Assert.Throws<SqliteException>(() => Store(fixture).CommitPreparedCancellation(fixture.JournalId));
            var pending = fixture.Scope.VerifyReadOnly(); Assert.Equal(JournalPhase.Prepared, pending.Journal.Phase);
            Assert.Equal(JournalPhase.Committed, pending.Latest.Phase); revision = pending.Latest.Revision;
            resultId = AttemptResultCodec.Read(pending.Latest.Root, pending.Attempt)!.ResultId;
            Assert.Equal(0, fixture.Base.Count("attempt_receipts")); Assert.True(fixture.Base.Count("resource_isolations") > 0);
            Assert.Equal("accepted", OperationStatus(fixture, state.Journal.OperationId));
            Assert.False(File.Exists(fixture.Marker + ".output-ready"));
        }
        finally { fixture.Base.Execute("DROP TRIGGER fail_attempt_terminal;"); }
        var result = Store(fixture).CommitPreparedCancellation(fixture.JournalId);
        var completed = fixture.Scope.VerifyReadOnly(); Assert.Equal(revision, completed.Journal.Revision);
        Assert.Equal(resultId, AttemptResultCodec.Read(completed.Latest.Root, completed.Attempt)!.ResultId);
        Assert.Equal(LifecycleOutcome.Cancelled, result.Facts.Lifecycle); Assert.Equal("completed", OperationStatus(fixture, state.Journal.OperationId));
        Assert.Equal(1, fixture.Base.Count("attempt_receipts")); Assert.Equal(0, fixture.Base.Count("resource_isolations"));
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
        Assert.Equal(EffectGateState.Entered, fixture.Scope.VerifyReadOnly().Gate.State); Assert.Equal(0, fixture.Base.Count("attempt_receipts"));
    }

    private static AttemptTerminalStore Store(StepWriterFixture fixture) =>
        new(fixture.Base.Source.Instance, fixture.Base.Source.Database, new(), fixture.Base.Source.Lease);
    private static void AssertSame((string[] Rows, string[] Files) before, StepWriterFixture fixture)
    {
        var after = fixture.Base.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }
    private static string OperationStatus(StepWriterFixture fixture, Guid operation)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Base.Source.Database.DatabasePath, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT status FROM operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operation.ToString("D")); return (string)command.ExecuteScalar()!;
    }
}
