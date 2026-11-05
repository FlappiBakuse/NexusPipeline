using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class StartAcceptanceTests
{
    [Fact(DisplayName = "An Accepted Start Replays Its Original References Before Reading Changed Definitions Settings Or Missing Images")]
    public void ReplaysOriginalInputs()
    {
        using var fixture = new Fixture();
        var accepted = fixture.Accept();
        Assert.True(accepted.Acceptance.IsNew);
        fixture.ReleasePreparation();
        fixture.Source.Disable(fixture.Source.BindingA);
        fixture.ChangeSettings();
        File.Delete(fixture.Source.OtherImage);
        var before = fixture.Source.Snapshot();
        var replay = Assert.IsType<AttemptAcceptanceResult>(fixture.Acceptance.LookupStart(fixture.Identity));
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(accepted.Plan, replay.Plan); Assert.Equal(accepted.JournalId, replay.JournalId);
        Assert.Equal(replay, fixture.Accept());
        var after = fixture.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    [Theory(DisplayName = "A Reused Start Operation Cannot Borrow Another Target Subject Or Expected Revision")]
    [InlineData("target")]
    [InlineData("subject")]
    [InlineData("revision")]
    public void RejectsChangedIdentity(string changed)
    {
        using var fixture = new Fixture(); fixture.Accept();
        var request = changed switch
        {
            "target" => fixture.Request with { Target = new BindingTarget(fixture.Source.BindingA) },
            "revision" => fixture.Request with { ExpectedCatalogRevision = fixture.Request.ExpectedCatalogRevision + 1 },
            _ => fixture.Request
        };
        var identity = ExecutionStartIdentity.Create(fixture.Identity.OperationId,
            changed == "subject" ? "another-subject" : fixture.Identity.SubjectId, request);
        var before = fixture.Source.Snapshot();
        Assert.Throws<OperationConflictException>(() => fixture.Acceptance.LookupStart(identity));
        Assert.Throws<OperationConflictException>(() => fixture.Acceptance.AcceptStart(identity, fixture.Draft, fixture.Prepared, DateTimeOffset.UtcNow));
        var after = fixture.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    [Theory(DisplayName = "A Real Catalog Or Settings Change Rejects New Start Inside The Acceptance Transaction Without Consuming Its Operation")]
    [InlineData(false)]
    [InlineData(true)]
    public void RechecksAcceptedRevisions(bool settings)
    {
        using var fixture = new Fixture();
        if (settings) fixture.ChangeSettings(); else fixture.Source.Disable(fixture.Source.BindingA);
        var before = fixture.Source.Snapshot();
        Assert.Equal(settings ? "execution_settings_revision_conflict" : "execution_catalog_revision_conflict",
            Assert.Throws<ExecutionSelectionException>(() => fixture.Accept()).Message);
        Assert.Null(fixture.Acceptance.LookupStart(fixture.Identity));
        var after = fixture.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    [Fact(DisplayName = "A Real Start Claim Insert Failure Rolls Back Operation Journal Writer Gate And Every Resource Mode Together")]
    public void RevertsClaimFailure()
    {
        using var fixture = new Fixture();
        fixture.Execute("CREATE TRIGGER fixture_fail_start_claim BEFORE INSERT ON journal_resource_claims BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        var before = fixture.Source.Snapshot();
        try
        {
            Assert.Throws<SqliteException>(() => fixture.Accept());
            Assert.Null(fixture.Acceptance.LookupStart(fixture.Identity));
            var after = fixture.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        }
        finally { fixture.Execute("DROP TRIGGER fixture_fail_start_claim;"); }
        Assert.True(fixture.Accept().Acceptance.IsNew);
    }

    [Fact(DisplayName = "An Unaccepted Disposed Draft Cannot Publish Another Valid Prepared Artifact")]
    public void RejectsDisposedDrafts()
    {
        using var fixture = new Fixture(); fixture.Draft.Dispose();
        var before = fixture.Source.Snapshot();
        Assert.Throws<ObjectDisposedException>(() => fixture.Accept());
        Assert.Null(fixture.Acceptance.LookupStart(fixture.Identity));
        var after = fixture.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    [Theory(DisplayName = "Another Target Or Fresh Catalog Draft Cannot Borrow An Independently Valid Prepared Plan")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsBorrowedPlans(bool freshCatalog)
    {
        using var fixture = new Fixture();
        if (freshCatalog) fixture.Source.Disable(fixture.Source.BindingA);
        var request = fixture.Source.Request(new BindingTarget(freshCatalog ? fixture.Source.OtherBinding : fixture.Source.BindingA));
        using var draft = fixture.Source.Builder.Prepare(request, PlanBuilderFixture.CreatedAt);
        var identity = ExecutionStartIdentity.Create(fixture.Identity.OperationId, fixture.Identity.SubjectId, request);
        var before = fixture.Source.Snapshot();
        Assert.Equal("execution_start_plan_unbound", Assert.Throws<IOException>(() => fixture.Acceptance.AcceptStart(
            identity, draft, fixture.Prepared, DateTimeOffset.UtcNow)).Message);
        Assert.Null(fixture.Acceptance.LookupStart(identity));
        var after = fixture.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    [Fact(DisplayName = "A Committed Start Remains Replayable When Its Initial Physical Journal Has Not Yet Been Materialized")]
    public void ReplaysUnmaterializedJournal()
    {
        using var fixture = new Fixture();
        var acceptance = new RunAttemptAcceptance(fixture.Source.Instance, new(), fixture.Source.Database, new FailAfterCommit());
        Assert.Equal("fixture_acceptance_committed", Assert.Throws<IOException>(() => acceptance.AcceptStart(
            fixture.Identity, fixture.Draft, fixture.Prepared, DateTimeOffset.UtcNow)).Message);
        var replay = Assert.IsType<AttemptAcceptanceResult>(fixture.Acceptance.LookupStart(fixture.Identity));
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(fixture.Prepared.Reference, replay.Plan);
        Assert.ThrowsAny<IOException>(() => new JournalStore(fixture.Source.Instance, new()).Read(replay.JournalId));
        Assert.Equal(replay, fixture.Accept());
    }

    private sealed class Fixture : IDisposable
    {
        internal PlanBuilderFixture Source { get; } = new();
        internal ExecutionSelectionRequest Request { get; }
        internal OperationIdentity Identity { get; }
        internal ExecutionPlanDraft Draft { get; }
        internal PreparedExecutionPlan Total { get; }
        internal PreparedAttemptPlan Prepared { get; }
        internal RunAttemptAcceptance Acceptance => new(Source.Instance, new(), Source.Database);

        internal Fixture()
        {
            try
            {
                Request = Source.Request(new BindingTarget(Source.OtherBinding));
                Identity = ExecutionStartIdentity.Create(Guid.NewGuid(), "start-fixture", Request);
                Draft = Source.Builder.Prepare(Request, PlanBuilderFixture.CreatedAt);
                Total = Source.Builder.Freeze(Draft);
                var total = Total.Plan.ReadWire(); var attemptId = Guid.NewGuid().ToString("D");
                var journalId = Guid.NewGuid();
                var start = new NexusPipeline.Platform.Storage.ImmutableBlobStore(Source.Instance, new()).PersistPlan(
                    StartRequestCodec.Encode(Identity, Request, Guid.Parse(total.RunId), journalId));
                var candidate = new RunAttemptPlan
                {
                    Format = "nexus.execution.run-attempt-plan", Version = 1, InstanceId = total.InstanceId,
                    OperationId = Identity.OperationId.ToString("D"), JournalId = journalId.ToString("D"), RunId = total.RunId,
                    StartRequest = new() { RelativePath = start.RelativePath, SizeBytes = start.SizeBytes, Sha256 = start.Sha256 },
                    AttemptId = attemptId, AttemptOrdinal = 1, ConfigurationPlans = [], InitialLogs = [],
                    ExecutionPlan = new() { RelativePath = Total.Reference.RelativePath, SizeBytes = Total.Reference.SizeBytes, Sha256 = Total.Reference.Sha256 },
                    OutputDirectory = $"runs/{total.RunId}/{attemptId}", LogDirectory = $"logs/{total.RunId}/{attemptId}", ResourceKeys = total.ResourceKeys
                };
                Prepared = new AttemptPlanCodec(Source.Instance, new(), new(Source.Instance, new())).Freeze(candidate);
            }
            catch { Prepared?.Dispose(); Total?.Dispose(); Draft?.Dispose(); Source.Dispose(); throw; }
        }

        internal AttemptAcceptanceResult Accept() => Acceptance.AcceptStart(Identity, Draft, Prepared, DateTimeOffset.UtcNow);
        internal void ChangeSettings()
        {
            var before = Source.Settings.Read();
            var change = new HostSettingsChange(before.Revision, before.Values with { HistoryRetentionDays = before.Values.HistoryRetentionDays + 1 });
            Source.Settings.Commit(NexusPipeline.Platform.Storage.Sqlite.HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
        }
        internal void Execute(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Source.Database.DatabasePath, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        internal void ReleasePreparation() { Prepared.Dispose(); Total.Dispose(); Draft.Dispose(); }
        public void Dispose() { ReleasePreparation(); Source.Dispose(); }
    }

    private sealed class FailAfterCommit : IAttemptPersistenceObserver
    {
        public void Reached(AttemptPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point == AttemptPersistencePoint.AcceptanceCommitted) throw new IOException("fixture_acceptance_committed");
        }
    }
}
