using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SnapshotCompletionTests
{
    [Theory(DisplayName = "Concurrent Hold Updates Preserve Verified Publication And Current Holds Without Repeating Evidence Or The Registry Commit")]
    [InlineData("add")]
    [InlineData("replace")]
    [InlineData("remove")]
    public void PublishesConcurrentHolds(string change)
    {
        using var fixture = new SnapshotCompletionFixture();
        var run = fixture.Run; var index = run.Index;
        var original = new RecoveryResourceHold(Guid.NewGuid(), "completion:state", "fixture_original");
        if (change != "add") index.UpdateResourceHolds(RecoveryHoldOwner.CompletionActions, [original]);
        var otherHolds = index.Current.ResourceHolds.Where(item => item.Owner != RecoveryHoldOwner.CompletionActions).ToArray();
        RecoveryResourceHold[] changed = change == "remove" ? [] : [original with { ReasonCode = "fixture_current" }];
        var source = run.Source; long holdRevision = 0;
        var actual = new InterleavedEvidence(new RecoveryJournalRegistry(source.Database),
            new SnapshotCompletionEvidence(source.Instance, source.Database, new()), () =>
            {
                index.UpdateResourceHolds(RecoveryHoldOwner.CompletionActions, changed);
                holdRevision = index.Current.Revision;
            });
        var result = run.Lease.PublishExecution(() => new RecoveryCoordinator(actual, actual, index).VerifyAndPublish());
        Assert.True(result.IsReady); Assert.True(result.Revision > holdRevision);
        Assert.Equal(otherHolds, result.ResourceHolds.Where(item => item.Owner != RecoveryHoldOwner.CompletionActions));
        Assert.Equal(changed.Select(item => item with { Owner = RecoveryHoldOwner.CompletionActions }),
            result.ResourceHolds.Where(item => item.Owner == RecoveryHoldOwner.CompletionActions));
        Assert.Equal(1, actual.Verifications); Assert.Equal(1, actual.Publications);
        Assert.True(((IRecoveryRegistry)new RecoveryJournalRegistry(source.Database)).ReadSnapshot().IsReady);
        run.VerifyOriginal();
    }

    [Theory(DisplayName = "A New Unknown State Verified Publication Or Isolation During Evidence Still Rejects Publication And Preserves Its Block")]
    [InlineData("unknown")]
    [InlineData("publish")]
    [InlineData("isolation")]
    public void RejectsChangedVerification(string change)
    {
        using var fixture = new SnapshotCompletionFixture();
        var run = fixture.Run; var index = run.Index; var source = run.Source;
        var isolation = new ResourceIsolation("fixture:changed", Guid.NewGuid(), "fixture_unverified");
        var actual = new InterleavedEvidence(new RecoveryJournalRegistry(source.Database),
            new SnapshotCompletionEvidence(source.Instance, source.Database, new()), () =>
            {
                if (change == "unknown") index.BlockUnknown("recovery_verification_in_progress");
                else index.PublishVerified(index.Current.Revision, index.Current.Revision + 1,
                    change == "isolation" ? [isolation] : index.Current.Isolations);
            });
        var error = Assert.Throws<InvalidOperationException>(() => run.Lease.PublishExecution(() =>
            new RecoveryCoordinator(actual, actual, index).VerifyAndPublish()));
        Assert.Equal("recovery_index_revision_conflict", error.Message);
        Assert.False(index.Current.IsReady); Assert.Empty(index.Current.SnapshotCompletions);
        if (change == "isolation") Assert.Contains(isolation, index.Current.Isolations);
        Assert.Equal(1, actual.Verifications); Assert.Equal(1, actual.Publications);
        run.VerifyOriginal();
    }

    [Theory(DisplayName = "Snapshot Completion Never Uses A Quarantined Unknown Entered Foreign Or Changed Scope As An Ordinary Continuation")]
    [InlineData("unknown")]
    [InlineData("quarantined")]
    [InlineData("entered")]
    [InlineData("subject")]
    [InlineData("child-reference")]
    [InlineData("restored-scene")]
    public void RejectsUnsafeContinuation(string fault)
    {
        using var fixture = new SnapshotCompletionFixture(enterParent: fault == "entered");
        var source = fixture.Run.Source; var operation = Guid.NewGuid();
        if (fault == "unknown") fixture.Run.Index.BlockUnknown("fixture_unknown");
        if (fault == "quarantined") fixture.Quarantine();
        if (fault == "child-reference") fixture.Run.Execute("UPDATE configuration.configuration_run_resources SET plan_digest='" + new string('0', 64) +
            "',plan_path='blobs/plans/" + new string('0', 64) + $".json' WHERE session_id='{fixture.SessionId:D}';");
        var target = Path.Combine(fixture.Run.Request.Resources[0].Target.TargetPath, "settings.bin");
        var original = File.Exists(target) ? File.ReadAllBytes(target) : null;
        if (fault == "restored-scene") File.WriteAllBytes(target, [77]);
        var counts = Counts(fixture.Run); var revision = fixture.Session.Revision;
        var before = new JournalStore(source.Instance, new()).Read(fixture.ParentId).Export();
        try
        {
            var error = Record.Exception(() => fixture.Completion.CompleteInterrupted(operation,
                fault == "subject" ? "another-subject" : ConfigurationRunFixture.Subject, fixture.SessionId));
            Assert.True(error is IOException or ConfigurationException or OperationConflictException, error?.ToString() ?? "The unverified completion was accepted");
            Assert.Equal(counts, Counts(fixture.Run)); Assert.Equal(revision, fixture.Session.Revision);
            Assert.Null(fixture.Session.CompletionOperationId); Assert.Empty(fixture.Run.Index.Current.SnapshotCompletions);
            Assert.Equal(before, new JournalStore(source.Instance, new()).Read(fixture.ParentId).Export());
            Assert.NotEmpty(new RecoveryJournalRegistry(source.Database).ReadSnapshot().Isolations);
        }
        finally
        {
            if (fault == "restored-scene") { if (original is null) File.Delete(target); else File.WriteAllBytes(target, original); }
        }
    }

    [Fact(DisplayName = "A Failed Snapshot Completion Transaction Preserves Its Exact Intent And Requires Fresh Proof Before The Same Operation Can Retry")]
    public void RevalidatesFailedCompletion()
    {
        using var fixture = new SnapshotCompletionFixture();
        var run = fixture.Run; var operation = Guid.NewGuid(); var before = Counts(run);
        run.Execute("CREATE TRIGGER configuration.fixture_finish_failure BEFORE INSERT ON configuration_run_sync_items BEGIN SELECT RAISE(ABORT,'fixture_finish_failure'); END;");
        Assert.Throws<SqliteException>(() => fixture.Completion.CompleteInterrupted(operation, ConfigurationRunFixture.Subject, fixture.SessionId));
        Assert.Equal(before, Counts(run)); Assert.False(run.Index.Current.IsReady); Assert.Null(fixture.Session.CompletionOperationId);
        var journal = new JournalStore(run.Source.Instance, new()).Read(fixture.ParentId);
        var intent = Assert.Single(SnapshotIntentCodec.Read(journal.Root));
        Assert.Equal(operation, intent.CompletionOperationId); Assert.Equal(fixture.Session.Revision, intent.GroupRevision);
        Assert.Throws<ConfigurationException>(() => fixture.Completion.CompleteInterrupted(operation, ConfigurationRunFixture.Subject, fixture.SessionId));
        run.Execute("DROP TRIGGER configuration.fixture_finish_failure;");
        fixture.Publish(completionEvidence: true);
        var grant = Assert.Single(run.Index.Current.SnapshotCompletions);
        Assert.Equal(operation, grant.CompletionOperationId);
        Assert.Throws<OperationConflictException>(() => fixture.Completion.CompleteInterrupted(Guid.NewGuid(), ConfigurationRunFixture.Subject, fixture.SessionId));
        fixture.Publish(completionEvidence: true);
        var result = fixture.Completion.CompleteInterrupted(operation, ConfigurationRunFixture.Subject, fixture.SessionId);
        Assert.True(result.IsNew); Assert.Equal(ConfigurationRunPhase.Interrupted, result.Session.Phase);
        Assert.Empty(run.Index.Current.ResourceHolds); Assert.Empty(run.Index.Current.SnapshotCompletions);
        Assert.Equal(journal.Export(), new JournalStore(run.Source.Instance, new()).Read(fixture.ParentId).Export());
        Assert.False(fixture.Completion.CompleteInterrupted(operation, ConfigurationRunFixture.Subject, fixture.SessionId).IsNew);
        run.VerifyOriginal(); ConfigurationRunInventory.VerifyReadOnly(run.Source.Instance, run.Source.Database, new());
    }

    [Fact(DisplayName = "A Parent Quarantined After Its Completion Proof Cannot Publish Using The Previously Captured Snapshot Sample")]
    public void RejectsQuarantinedParents()
    {
        using var fixture = new SnapshotCompletionFixture();
        var run = fixture.Run; var operation = Guid.NewGuid();
        new WriterAdmissionStore(run.Source.Instance, run.Source.Database, new()).CloseRunAdmission(fixture.SessionId, 0, requireNoJobs: true);
        var session = fixture.Session;
        new SnapshotCompletionOwner(run.Source.Instance, run.Source.Database, new(), run.Lease).Prepare(operation,
            ConfigurationRunFixture.Subject, session, () => run.Index.BlockUnknown("fixture_prepare_completion"));
        fixture.Publish(completionEvidence: true);
        var completion = Assert.Single(run.Index.Current.SnapshotCompletions);
        using var sample = new RunProgressCapture(run.Source.Instance, run.Source.Database, new()).ReadRestored(session);
        fixture.Quarantine();
        var before = Counts(run);
        Assert.Throws<IOException>(() => new RunProgressStore(run.Source.Instance, run.Source.Database, new()).Publish(operation,
            ConfigurationRunFixture.Subject, sample, ConfigurationRunPhase.Interrupted, completion));
        Assert.Equal(before, Counts(run)); Assert.Null(fixture.Session.CompletionOperationId);
        Assert.Empty(run.Index.Current.SnapshotCompletions); run.VerifyOriginal();
    }

    private static long[] Counts(ConfigurationRunFixture fixture) => [fixture.Count("operations"), fixture.Count("configuration_revisions"),
        fixture.Count("configuration_run_sync_items"), fixture.Count("journal_writer_jobs"), fixture.Count("journal_writer_job_closures")];

    private sealed class InterleavedEvidence(IRecoveryRegistry registry, ISnapshotCompletionEvidence evidence, Action change)
        : IRecoveryRegistry, ISnapshotCompletionEvidence
    {
        internal int Verifications { get; private set; }
        internal int Publications { get; private set; }
        public RecoveryRegistrySnapshot ReadSnapshot() => registry.ReadSnapshot();
        public void VerifyAll(IReadOnlyList<RegisteredJournal> registered)
        {
            evidence.VerifyAll(registered); Verifications++;
            Task.Run(change).GetAwaiter().GetResult();
        }
        public IReadOnlyList<SnapshotCompletionProjection> ReadPendingCompletions(RecoveryRegistrySnapshot verified) =>
            evidence.ReadPendingCompletions(verified);
        public RecoveryRegistrySnapshot PublishVerified(long expectedRevision, long minimumRevision)
        {
            Publications++;
            return registry.PublishVerified(expectedRevision, minimumRevision);
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SnapshotCompletionFixture : IDisposable
{
    internal ConfigurationRunFixture Run { get; } = new(additional: true);
    internal Guid SessionId { get; } = Guid.NewGuid();
    internal Guid ParentId { get; }
    internal RunCompletionService Completion { get; }

    internal SnapshotCompletionFixture(bool enterParent = false)
    {
        using (var root = Run.Acquire(Run.Request))
        using (var prepared = Run.Preparation.Prepare(root, Run.Owner, Run.Execution.Plan, Run.StepId, Run.Request))
        {
            var parent = Run.Parent(prepared); ParentId = Guid.Parse(parent.JournalId); Run.Accept(parent);
            var operation = ConfigurationRunStore.BeginIdentity(SessionId, ConfigurationRunFixture.Subject, ParentId, Run.StepId, prepared);
            new ConfigurationRunStore(Run.Source.Instance, Run.Source.Database, new()).Begin(operation, ParentId, Run.StepId, prepared, DateTimeOffset.UtcNow);
        }
        var registry = new RecoveryJournalRegistry(Run.Source.Database);
        var registered = registry.ReadSnapshot().Journals.Single(item => item.JournalId == ParentId);
        var gate = new JournalEffectGate(Run.Source.Database);
        if (enterParent) gate.Enter(registered, 1); else gate.SealUnentered(registered, 1);
        foreach (var resource in Session.Resources)
            Assert.Equal(ConfigurationRecoveryResult.NotExecuted, new ConfigurationEditExchange(Run.Source.Instance, Run.Source.Database, new()).Restore(resource.JournalId));
        Publish();
        Completion = new(Run.Source.Instance, Run.Source.Database, new(), Run.Lease, Run.Resources, Run.Index);
    }

    internal ConfigurationRunStatus Session => new ConfigurationRunStore(Run.Source.Instance, Run.Source.Database, new())
        .Read(SessionId, ConfigurationRunFixture.Subject)!;

    internal void Publish(bool completionEvidence = false)
    {
        var source = Run.Source;
        IRecoveryEvidence evidence = completionEvidence ? new SnapshotCompletionEvidence(source.Instance, source.Database, new())
            : new JournalRecoveryEvidence(source.Instance, new(source.Instance, new()));
        new RecoveryCoordinator(new RecoveryJournalRegistry(source.Database), evidence, Run.Index).VerifyAndPublish();
        ConfigurationRecoveryHolds.Publish(source.Database, Run.Index);
    }

    internal void Quarantine()
    {
        var source = Run.Source;
        new PreparedRecoverySession(source.Instance, ParentId, new(), source.Database, () =>
        {
            var observed = new JournalWriterStore(source.Database).ObserveForCurrentExecutor(ParentId);
            return new(source.Instance.InstanceId, ParentId, observed.IsComplete, observed.State);
        }).Quarantine("attempt_prepared_no_effect_unverified");
        Publish();
    }

    public void Dispose() => Run.Dispose();
}
