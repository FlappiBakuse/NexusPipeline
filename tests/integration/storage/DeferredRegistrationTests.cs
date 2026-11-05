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
        var scope = fixture.Scope(0);
        var controller = new ConfigurationRunController(source.Instance, source.Database, new(), source.Lease, fixture.Base.Index,
            sessionId, ExecutionSequentialFixture.Subject, fixture.Accepted.Prepared.Configurations[stepId], scope, deferRegistration: true);
        Assert.Equal(0, fixture.Base.Count("journal_writer_jobs"));
        fixture.Base.Execute("CREATE TRIGGER fixture_child_registration BEFORE INSERT ON journal_writer_jobs " +
            "WHEN EXISTS(SELECT 1 FROM configuration_run_resources r WHERE r.journal_id=NEW.journal_id) " +
            "BEGIN SELECT RAISE(ABORT,'fixture_child_registration'); END;");
        try
        {
            Assert.Throws<SqliteException>(() => controller.RegisterPreparedJob());
            Assert.Single(fixture.State.Steps); Assert.Equal(1, fixture.Base.Count("journal_writer_jobs"));
            Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots")); Assert.False(controller.HasConfigurationJobRegistration);
            scope.RequirePrepared();
            Assert.Throws<IOException>(() => fixture.Scope(0));
            var before = source.Snapshot();
            Assert.Equal("configuration_writer_registration_incomplete", Assert.Throws<IOException>(() => controller.Start()).Message);
            var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        }
        finally { fixture.Base.Execute("DROP TRIGGER fixture_child_registration;"); }
        if (retryRegistration)
        {
            controller.RegisterPreparedJob(); controller.RegisterPreparedJob();
            Assert.True(controller.HasConfigurationJobRegistration); Assert.Equal(2, fixture.Base.Count("journal_writer_jobs"));
        }
        var binding = StepResultCodec.Binding(fixture.State.Latest.Root, Assert.Single(fixture.State.Steps));
        using (var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, controller))
        {
            var stopped = await controller.StopWriterAsync(TimeSpan.FromSeconds(10));
            Assert.False(stopped.WasExecuted); Assert.Null(stopped.Closure.Root); Assert.Empty(stopped.Closure.Family);
            Assert.Equal(0, stopped.Closure.RunElapsedMilliseconds);
            var expectedJournals = retryRegistration ? runs.Read(sessionId, ExecutionSequentialFixture.Subject)!.Resources.Select(item => item.JournalId) :
                new[] { fixture.ParentId };
            Assert.True(expectedJournals.SequenceEqual(stopped.Closure.JournalIds));
            _ = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        }
        var job = Assert.Single(fixture.State.Steps);
        var parentClosure = new JobClosureStore(source.Instance, source.Database, new()).ReadForExecutionEvidence(
            fixture.ParentId, job.WriterId, job.JobName, job.SessionId);
        Assert.NotNull(parentClosure); Assert.False(parentClosure.WasExecuted); Assert.Empty(parentClosure.Family);
        Assert.Equal(0, parentClosure.RunElapsedMilliseconds); Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(retryRegistration ? 2 : 1, fixture.Base.Count("journal_writer_job_closures"));
        fixture.Terminal.PrepareStopped(fixture.ParentId, LifecycleOutcome.Interrupted, DispatchIntentCodec.InterruptionReason);
        foreach (var id in fixture.Sessions.Values)
            foreach (var resource in runs.Read(id, ExecutionSequentialFixture.Subject)!.Resources)
                Assert.NotEqual(ConfigurationRecoveryResult.Quarantined, exchange.Restore(resource.JournalId));
        new RecoveryCoordinator(new RecoveryJournalRegistry(source.Database),
            new SnapshotCompletionEvidence(source.Instance, source.Database, new()), fixture.Base.Index).VerifyAndPublish();
        foreach (var (id, session) in fixture.Sessions)
        {
            if (id == stepId && retryRegistration) await controller.FinishAsync(Guid.NewGuid(), interrupted: true);
            else new RunCompletionService(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources, fixture.Base.Index)
                .CompleteOwnedInterrupted(Guid.NewGuid(), ExecutionSequentialFixture.Subject, session, fixture.Accepted.Prepared.Configurations[id]);
        }
        if (!retryRegistration) controller.ReleaseUnregisteredAfterCompletion();
        controller.Dispose();
        var receipt = fixture.Terminal.CommitPreparedStop(fixture.ParentId, LifecycleOutcome.Interrupted, DispatchIntentCodec.InterruptionReason);
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        Assert.Equal(RecoveryOutcome.NotRequired, receipt.Facts.Recovery); Assert.Equal(EffectGateState.Sealed, fixture.State.Gate.State);
        Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration)); Assert.Equal(originalBytes, File.ReadAllBytes(source.Configuration));
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots")); Assert.Equal(1, fixture.Base.Count("attempt_receipts"));
    }

    [Fact(DisplayName = "The Actual Consumer Retains A Parent Registered Job When The Child Insert Fails And Completes Without Resume")]
    public async Task CollectsUnresumedParent()
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: true); var source = fixture.Source;
        var original = PhysicalObjectIdentity.Read(source.Configuration); var bytes = File.ReadAllBytes(source.Configuration);
        fixture.Base.Execute("CREATE TRIGGER fixture_runtime_child_registration BEFORE INSERT ON journal_writer_jobs " +
            "WHEN EXISTS(SELECT 1 FROM configuration_run_resources r WHERE r.journal_id=NEW.journal_id) " +
            "BEGIN SELECT RAISE(ABORT,'fixture_runtime_child_registration'); END;");
        try { await Assert.ThrowsAsync<SqliteException>(() => fixture.Consumer.RunNextAsync()); }
        finally { fixture.Base.Execute("DROP TRIGGER fixture_runtime_child_registration;"); }
        var state = fixture.State; var job = Assert.Single(state.Steps);
        Assert.Equal(JournalPhase.Committed, state.Journal.Phase); Assert.Equal(EffectGateState.Sealed, state.Gate.State);
        Assert.Equal(1, fixture.Base.Count("journal_writer_jobs")); Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(1, fixture.Base.Count("journal_writer_job_closures")); Assert.False(File.Exists(fixture.Marker));
        var closure = new JobClosureStore(source.Instance, source.Database, new()).ReadForExecutionEvidence(
            fixture.Accepted.JournalId, job.WriterId, job.JobName, job.SessionId);
        Assert.NotNull(closure); Assert.False(closure.WasExecuted); Assert.Null(closure.Root); Assert.Empty(closure.Family);
        Assert.Equal(0, closure.RunElapsedMilliseconds);
        var receipt = new AttemptReceiptStore(source.Database).Read(Guid.Parse(state.Attempt.AttemptId))!;
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        Assert.Equal(RecoveryOutcome.NotRequired, receipt.Facts.Recovery);
        Assert.Equal(0, fixture.Consumer.PendingReconciliationCount); Assert.Equal(0, fixture.Base.Count("resource_isolations"));
        Assert.True(fixture.Base.Index.Current.IsReady); Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.Equal(bytes, File.ReadAllBytes(source.Configuration));
    }
}
