using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RuntimeRecoveryTests
{
    [Fact(DisplayName = "A Recovered Preparation Failure Publishes Its Terminal History Without Restart")]
    public async Task PublishesPreparationFailure()
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: true);
        var source = fixture.Source;
        var projection = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new()));
        var consumer = new ExecutionRuntimeConsumer(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index, fixture.Base.Inbox, projection);
        fixture.Base.Execute("CREATE TRIGGER fixture_preparation_failure BEFORE INSERT ON configuration_run_sessions " +
            "BEGIN SELECT RAISE(ABORT, 'fixture_preparation_failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => consumer.RunNextAsync());
        Assert.Equal(1, fixture.Base.Count("attempt_receipts"));
        Assert.Equal(1, fixture.Base.Count("run_records"));
        Assert.True(fixture.Base.Count("account_run_records") > 0);
        Assert.Equal(0, consumer.PendingReconciliationCount);
        Assert.False(File.Exists(fixture.Marker));
    }

    [Theory(DisplayName = "A Preparation Cancellation Publishes Account History Before Restart And Releases Run Ownership")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublishesPreparedCancellation(bool afterStepPreparation, bool enteredEffect)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: true);
        var source = fixture.Source;
        AttemptReceipt receipt;
        if (afterStepPreparation)
        {
            var owned = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
            using var session = new AttemptRuntimeSession(source.Instance, source.Database, new(), source.Lease,
                fixture.Base.Resources, fixture.Base.Index, owned);
            session.PrepareConfigurationGroups();
            session.PrepareNextStep(session.StepIds[0]);
            if (enteredEffect) session.EnterEffect(session.StepIds[0]);
            fixture.Cancel();
            session.FreezeDispatch(ExecutionStopReason.Cancelled);
            await session.CollectRemainingAsync();
            session.CloseUndispatched();
            if (enteredEffect) session.MarkApplied();
            await session.RestoreRemainingAsync(ExecutionStopReason.Cancelled);
            receipt = session.Commit(ExecutionStopReason.Cancelled);
        }
        else
        {
            fixture.Cancel();
            receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        }
        var finality = new ExecutionRunFinality(source.Instance, source.Database, new());
        Assert.True(finality.IsFinal(receipt));
        new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease, finality).OnCommitted(receipt);
        Assert.Equal(1, fixture.Base.Count("run_records"));
        Assert.True(fixture.Base.Count("account_run_records") > 0);
        Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        Assert.False(File.Exists(fixture.Marker));
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Fact(DisplayName = "Cancellation During Lazy Step Preparation Reuses Its Durable Completion Intent And Original Borrow After Sql Rollback")]
    public async Task ReusesCompletionIntent()
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: true); var source = fixture.Source;
        var original = PhysicalObjectIdentity.Read(source.Configuration); var bytes = File.ReadAllBytes(source.Configuration);
        var owned = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
        var session = new AttemptRuntimeSession(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index, owned);
        session.PrepareConfigurationGroups();
        fixture.Cancel();
        var failingStep = session.StepIds[0];
        Assert.Equal("execution_writer_registration_closed",
            Assert.Throws<IOException>(() => session.PrepareNextStep(failingStep)).Message);
        session.FreezeDispatch(ExecutionStopReason.Cancelled);
        await session.CollectRemainingAsync(); session.CloseUndispatched();
        fixture.Base.Execute("CREATE TRIGGER fixture_owned_completion_failure BEFORE UPDATE OF phase ON configuration_run_sessions " +
            $"WHEN OLD.step_id='{failingStep}' AND NEW.phase=3 BEGIN SELECT RAISE(ABORT,'fixture_owned_completion_failure'); END;");
        Guid operation;
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => session.RestoreRemainingAsync(ExecutionStopReason.Cancelled));
            Assert.True(owned.Root.IsActive);
            Assert.False(fixture.Base.Index.Current.IsReady);
            var intents = SnapshotIntentCodec.Read(fixture.State.Latest.Root);
            operation = Assert.Single(intents).CompletionOperationId;
            Assert.Null(new ConfigurationRunStore(source.Instance, source.Database, new()).Read(intents[0].SessionId,
                ExecutionSequentialFixture.Subject)!.CompletionOperationId);
            Assert.Equal(0, fixture.Base.Count("attempt_receipts")); Assert.Equal(0, fixture.Base.Count("journal_writer_jobs"));
        }
        finally { fixture.Base.Execute("DROP TRIGGER fixture_owned_completion_failure;"); }
        await session.RestoreRemainingAsync(ExecutionStopReason.Cancelled);
        var finalIntents = SnapshotIntentCodec.Read(fixture.State.Latest.Root);
        Assert.Equal(operation, Assert.Single(finalIntents).CompletionOperationId);
        Assert.All(finalIntents, intent => Assert.Equal(intent.CompletionOperationId,
            new ConfigurationRunStore(source.Instance, source.Database, new()).Read(intent.SessionId, ExecutionSequentialFixture.Subject)!.CompletionOperationId));
        var receipt = session.Commit(ExecutionStopReason.Cancelled);
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        var finality = new ExecutionRunFinality(source.Instance, source.Database, new());
        Assert.True(finality.IsFinal(receipt));
        new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease, finality).OnCommitted(receipt);
        Assert.Equal(1, fixture.Base.Count("run_records"));
        Assert.Equal(bytes, File.ReadAllBytes(source.Configuration)); Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.True(fixture.Base.Index.Current.IsReady); Assert.Equal(0, fixture.Base.Count("resource_isolations"));
        session.Dispose();
    }

    [Theory(DisplayName = "An Independent Native Consumer Preserves An Unmaterialized Manual Edit And Its Exact Isolation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsManualIsolation(bool cancel)
    {
        var marker = "";
        using var fixture = new AdmissionPublicationFixture(source =>
        {
            var catalog = source.Definitions.Read(); var script = catalog.Scripts.Single(item => item.Id == source.OtherScriptId);
            var root = catalog.Roots.Single(item => item.Id == script.RootId); var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
            marker = Path.Combine(source.Root, "independent-native");
            var change = new AutomationChanges(catalog.Revision, [root with { Path = Path.GetDirectoryName(native)! }],
                [script with { Executable = new(root.Id, Path.GetFileName(native)), Arguments = ["process-output", "tail", marker] }], [], [], [], [], []);
            source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "independent-native", change), change, DateTimeOffset.UtcNow);
        });
        var admission = fixture.Admission; var source = admission.Source;
        var consumer = new ExecutionRuntimeConsumer(source.Instance, source.Database, new(), source.Lease, admission.Resources, admission.Index, admission.Inbox);
        var accepted = admission.Service.Start(Guid.NewGuid(), "independent-native", source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        if (cancel)
        {
            var state = AttemptWriterState.Read(source.Instance, source.Database, new(), accepted.JournalId);
            new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease).Cancel(
                ExecutionCancellationStore.Identity(Guid.NewGuid(), "independent-native", accepted.JournalId, state.Journal.OperationId,
                    Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), accepted.JournalId, DateTimeOffset.UtcNow);
        }
        var receipt = Assert.IsType<AttemptReceipt>(await consumer.RunNextAsync());
        Assert.Equal(cancel ? LifecycleOutcome.Cancelled : LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        Assert.Equal(!cancel, File.Exists(marker)); Assert.True(admission.Index.Current.IsReady);
        Assert.False(Directory.Exists(fixture.JournalDirectory));
        Assert.Equal(ConfigurationEditPhase.Prepared, fixture.Store.Read(fixture.Edit.SessionId, "admission-edit")!.Phase);
        var slot = ConfigurationRevisionService.ResourceKey(fixture.Request.Targets[0].Address);
        Assert.Contains(admission.Index.Current.Isolations, item => item.JournalId == fixture.Edit.Resources[0].JournalId && item.ResourceKey == slot);
        Assert.Null(admission.Resources.TryAcquire(Guid.NewGuid(), [new(slot, ResourceClaimMode.Mutation)]).Lease);
        var snapshot = new RecoveryJournalRegistry(source.Database).ReadSnapshot();
        Assert.Throws<IOException>(() => new JournalRecoveryEvidence(source.Instance, new(source.Instance, new())).VerifyAll(snapshot.Journals));
        Assert.Empty(admission.Index.Current.SnapshotCompletions); Assert.Equal(0, consumer.PendingReconciliationCount);
    }
}
