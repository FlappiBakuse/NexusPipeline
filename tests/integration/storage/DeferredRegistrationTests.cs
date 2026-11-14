using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class DeferredRegistrationTests
{
    [Theory(DisplayName = "A Failed Child Registration Retains The Actual Empty Job For Exact Retry Or Stopped Parent Only Completion")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainsFailedRegistration(bool retryRegistration)
    {
        using var fixture = new ExecutionSequentialFixture(configuration: true); var source = fixture.Source;
        var runs = new ConfigurationRunStore(source.Instance, source.Database, new());
        var exchange = new ConfigurationEditExchange(source.Instance, source.Database, new());
        foreach (var id in fixture.Sessions.Values)
            foreach (var resource in runs.Read(id, ExecutionSequentialFixture.Subject)!.Resources) exchange.Materialize(resource.JournalId);
        var original = PhysicalObjectIdentity.Read(source.Configuration); var originalBytes = File.ReadAllBytes(source.Configuration);
        var stepId = fixture.Steps[0].StepId; var sessionId = fixture.Sessions[stepId];
        var session = runs.Read(sessionId, ExecutionSequentialFixture.Subject)!;
        var stopEvidence = new RunStopEvidence(source.Instance, source.Database, new());
        var scope = fixture.Scope(0);
        var controller = new ConfigurationRunController(source.Instance, source.Database, new(), source.Lease, fixture.Base.Index,
            sessionId, ExecutionSequentialFixture.Subject, fixture.Accepted.Prepared.Configurations[stepId], scope, deferRegistration: true);
        Assert.Empty(fixture.State.Steps);
        Assert.True(stopEvidence.HasNoJobs(session));
        fixture.Base.Execute("CREATE TRIGGER main.fixture_child_registration BEFORE INSERT ON journal_writer_jobs " +
            "WHEN EXISTS(SELECT 1 FROM main.operation_journals j WHERE j.journal_id=NEW.journal_id AND j.operation_kind='configExchange') " +
            "BEGIN SELECT RAISE(ABORT,'fixture_child_registration'); END;");
        StepEvidenceBinding binding;
        try
        {
            var failure = Assert.Throws<SqliteException>(() => controller.RegisterPreparedJob());
            Assert.Contains("fixture_child_registration", failure.Message, StringComparison.Ordinal);
            var state = fixture.State;
            var job = Assert.Single(state.Steps);
            Assert.Equal(stepId, job.StepId);
            binding = StepResultCodec.Binding(state.Latest.Root, job);
            Assert.Null(new StepEvidenceReader(source.Instance, source.Database, new()).Context(binding).Root);
            var unregistered = runs.Read(sessionId, ExecutionSequentialFixture.Subject)!;
            Assert.True(stopEvidence.HasNoJobs(unregistered));
            Assert.Null(unregistered.PrimaryJobName);
            scope.RequirePrepared();
            Assert.Throws<IOException>(() => fixture.Scope(0));
            Assert.Equal("configuration_writer_registration_incomplete", Assert.Throws<IOException>(() => controller.Start()).Message);
            Assert.Equal(state.Latest.Export(), fixture.State.Latest.Export());
            Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration));
            Assert.Equal(originalBytes, File.ReadAllBytes(source.Configuration));
        }
        finally { fixture.Base.Execute("DROP TRIGGER main.fixture_child_registration;"); }
        if (retryRegistration)
        {
            controller.RegisterPreparedJob(); controller.RegisterPreparedJob();
            Assert.Equal(binding.JobName, runs.Read(sessionId, ExecutionSequentialFixture.Subject)!.PrimaryJobName);
            var afterRetry = fixture.State;
            Assert.Equal(binding, StepResultCodec.Binding(afterRetry.Latest.Root, Assert.Single(afterRetry.Steps)));
        }
        StoppedConfigurationEditor stopped;
        using (var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, controller))
        {
            stopped = await controller.StopWriterAsync(TimeSpan.FromSeconds(10));
            var expectedJournals = retryRegistration ? session.Resources.Select(item => item.JournalId) :
                new[] { fixture.ParentId };
            Assert.Equal(expectedJournals.Order(), stopped.Closure.JournalIds.Order());
            _ = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        }
        var closures = new JobClosureStore(source.Instance, source.Database, new());
        var parentClosure = closures.ReadForExecutionEvidence(fixture.ParentId, binding.WriterId, binding.JobName, binding.SessionId);
        Assert.NotNull(parentClosure);
        Assert.Equal(binding.ParentOperationId, parentClosure.OperationId);
        AssertUnresumed(parentClosure);
        foreach (var id in fixture.Sessions.Values)
            foreach (var resource in runs.Read(id, ExecutionSequentialFixture.Subject)!.Resources)
            {
                var child = closures.Read(resource.JournalId, binding.WriterId, binding.JobName, binding.SessionId);
                if (retryRegistration && id == sessionId)
                {
                    Assert.NotNull(child);
                    Assert.Equal(sessionId, child.OperationId);
                    AssertUnresumed(child);
                    Assert.Equal(stopped.Reference, closures.RequireStoredReference(resource.JournalId, child));
                }
                else Assert.Null(child);
            }
        fixture.Terminal.PrepareStopped(fixture.ParentId, LifecycleOutcome.Interrupted, DispatchIntentCodec.InterruptionReason);
        foreach (var id in fixture.Sessions.Values)
            foreach (var resource in runs.Read(id, ExecutionSequentialFixture.Subject)!.Resources)
                Assert.NotEqual(ConfigurationRecoveryResult.Quarantined, exchange.Restore(resource.JournalId));
        new RecoveryCoordinator(new RecoveryJournalRegistry(source.Database),
            new SnapshotCompletionEvidence(source.Instance, source.Database, new()), fixture.Base.Index).VerifyAndPublish();
        foreach (var (id, runSession) in fixture.Sessions)
        {
            if (id == stepId && retryRegistration) await controller.FinishAsync(Guid.NewGuid(), interrupted: true);
            else new RunCompletionService(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources, fixture.Base.Index)
                .CompleteOwnedInterrupted(Guid.NewGuid(), ExecutionSequentialFixture.Subject, runSession, fixture.Accepted.Prepared.Configurations[id]);
        }
        if (!retryRegistration) controller.ReleaseUnregisteredAfterCompletion();
        controller.Dispose();
        var receipt = fixture.Terminal.CommitPreparedStop(fixture.ParentId, LifecycleOutcome.Interrupted, DispatchIntentCodec.InterruptionReason);
        AssertInterrupted(fixture.State, receipt);
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(binding.AttemptId));
        Assert.Null(new StepEvidenceReader(source.Instance, source.Database, new()).Context(binding).Root);
        Assert.False(File.Exists(fixture.Marker + ".output-ready"));
        Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration)); Assert.Equal(originalBytes, File.ReadAllBytes(source.Configuration));
    }

    [Fact(DisplayName = "The Actual Consumer Retains A Parent Registered Job When The Child Insert Fails And Completes Without Resume")]
    public async Task CollectsUnresumedParent()
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: true); var source = fixture.Source;
        var original = PhysicalObjectIdentity.Read(source.Configuration); var bytes = File.ReadAllBytes(source.Configuration);
        fixture.Base.Execute("CREATE TRIGGER main.fixture_runtime_child_registration BEFORE INSERT ON journal_writer_jobs " +
            "WHEN EXISTS(SELECT 1 FROM main.operation_journals j WHERE j.journal_id=NEW.journal_id AND j.operation_kind='configExchange') " +
            "BEGIN SELECT RAISE(ABORT,'fixture_runtime_child_registration'); END;");
        try
        {
            var failure = await Assert.ThrowsAsync<SqliteException>(() => fixture.Consumer.RunNextAsync());
            Assert.Contains("fixture_runtime_child_registration", failure.Message, StringComparison.Ordinal);
        }
        finally { fixture.Base.Execute("DROP TRIGGER main.fixture_runtime_child_registration;"); }
        var state = fixture.State; var job = Assert.Single(state.Steps);
        var binding = StepResultCodec.Binding(state.Latest.Root, job);
        Assert.Null(new StepEvidenceReader(source.Instance, source.Database, new()).Context(binding).Root);
        Assert.False(File.Exists(fixture.Marker + ".output-ready"));
        var closure = new JobClosureStore(source.Instance, source.Database, new()).ReadForExecutionEvidence(
            fixture.Accepted.JournalId, job.WriterId, job.JobName, job.SessionId);
        Assert.NotNull(closure);
        Assert.Equal(binding.ParentOperationId, closure.OperationId);
        AssertUnresumed(closure);
        var receipt = new AttemptReceiptStore(source.Database).Read(binding.AttemptId);
        AssertInterrupted(state, receipt);
        Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
        Assert.Empty(fixture.Base.Index.Current.Isolations);
        Assert.True(fixture.Base.Index.Current.IsReady); Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.Equal(bytes, File.ReadAllBytes(source.Configuration));
    }

    private static void AssertUnresumed(FrozenJobClosure closure)
    {
        Assert.False(closure.WasExecuted); Assert.Null(closure.Root); Assert.Empty(closure.Family);
        Assert.Equal(0, closure.RunElapsedMilliseconds);
    }

    private static void AssertInterrupted(AttemptWriterState state, AttemptReceipt? receipt)
    {
        Assert.NotNull(receipt);
        Assert.Equal(state.Journal.JournalId, receipt.JournalId);
        Assert.Equal(state.Journal.OperationId, receipt.OperationId);
        Assert.Equal(state.Journal.PlanDigest, receipt.PlanDigest);
        Assert.Equal(Guid.Parse(state.Attempt.AttemptId), receipt.Facts.AttemptId);
        Assert.Equal(Guid.Parse(state.Attempt.RunId), receipt.Facts.RunId);
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
        Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        Assert.Equal(RecoveryOutcome.NotRequired, receipt.Facts.Recovery);
        Assert.Equal(JournalPhase.Committed, state.Journal.Phase);
        Assert.Equal(EffectGateState.Sealed, state.Gate.State);
    }
}
