using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SnapshotCompletionTests
{
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
}
