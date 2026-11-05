using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExecutionAdmissionTests
{
    private const string Subject = "execution-admission";

    [Fact(DisplayName = "A Real Admission Transfers Its Frozen Inputs And Borrowed Configurations To The Run Owner While The Caller Receives Only A Receipt")]
    public void TransfersFrozenOwnership()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var original = File.ReadAllBytes(source.Configuration); var log = File.ReadAllBytes(source.Log); var operation = Guid.NewGuid();
        var result = fixture.Service.Start(operation, Subject, source.Request(new BindingTarget(source.BindingB)), PlanBuilderFixture.CreatedAt);
        Assert.True(result.Acceptance.IsNew); Assert.Equal(1, fixture.Inbox.PendingCount); Assert.True(fixture.Index.Current.IsReady);
        Assert.NotEmpty(fixture.Index.Current.Isolations);
        var parent = Assert.Single(new RecoveryJournalRegistry(source.Database).ReadSnapshot().Journals, item => item.JournalId == result.JournalId);
        Assert.Equal(JournalPhase.Prepared, parent.Phase);
        using var owned = fixture.Take(operation);
        var total = owned.Prepared.Total.Plan.ReadWire(); var attempt = owned.Prepared.Attempt.Plan.ReadWire();
        Assert.Equal(result.Plan, owned.Prepared.Attempt.Reference); Assert.Equal(total.RunId, attempt.RunId);
        Assert.Equal(total.ResourceKeys, parent.ResourceKeys);
        Assert.Equal(ExecutionPlanResources.Claims(total).ToArray(), JournalResourceClaims.Read(source.Database, parent.JournalId)
            .Select(item => new ResourceClaim(item.Key, item.Value)).ToArray());
        Assert.Throws<IOException>(() => { using var image = new FileStream(source.Image, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        Assert.Throws<IOException>(() => { using var output = new FileStream(source.Log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        owned.Root.Dispose();
        var child = Assert.Single(owned.Prepared.Configurations).Value.Use(children => Assert.Single(children));
        Assert.Equal(child.Reference.RelativePath, Assert.Single(attempt.ConfigurationPlans).Plan.RelativePath);
        Assert.Equal(child.Reference.Sha256, Assert.Single(attempt.ConfigurationPlans).Plan.Sha256);
        Assert.Equal(original, File.ReadAllBytes(source.Configuration)); Assert.Equal(log, File.ReadAllBytes(source.Log));
        Assert.False(Directory.Exists(Path.Combine(source.Instance.RuntimeRoot, attempt.OutputDirectory)));
        Assert.Equal("recovery_pending", fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).ReasonCode);
    }

    [Fact(DisplayName = "Dry Run Uses The Same Saved Inputs Without Publishing Definitions Plans Journals Or Any External Effect")]
    public void PreviewsWithoutEffects()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var before = fixture.Counts(); var blobs = Directory.GetFiles(Path.Combine(source.Instance.RuntimeRoot, "blobs"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var configuration = File.ReadAllBytes(source.Configuration); var log = File.ReadAllBytes(source.Log);
        using var preview = fixture.Service.DryRun(source.Request(new BindingTarget(source.BindingB)), PlanBuilderFixture.CreatedAt);
        Assert.True(preview.Explanation.HasRunnable); Assert.False(preview.Explanation.HasBlockedItems); Assert.NotNull(preview.ReadPlan());
        Assert.Equal(before, fixture.Counts()); Assert.Equal(blobs, Directory.GetFiles(Path.Combine(source.Instance.RuntimeRoot, "blobs"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(fixture.Index.Current.Isolations); Assert.Equal(0, fixture.Inbox.PendingCount);
        Assert.Equal(configuration, File.ReadAllBytes(source.Configuration)); Assert.Equal(log, File.ReadAllBytes(source.Log));
    }

    [Theory(DisplayName = "Missing Saved Inputs And Undeclared Specialized Providers Cannot Become Accepted Starts")]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RequiresSavedInputs(bool specialized, bool saved)
    {
        using var fixture = new ExecutionAdmissionFixture(specialized, saved); var source = fixture.Source; var before = fixture.Counts();
        var request = source.Request(new BindingTarget(source.BindingB));
        using var preview = fixture.Service.DryRun(request, PlanBuilderFixture.CreatedAt);
        Assert.True(preview.Explanation.HasBlockedItems);
        Assert.Throws<ExecutionSelectionException>(() => fixture.Service.Start(Guid.NewGuid(), Subject, request, PlanBuilderFixture.CreatedAt));
        Assert.Equal(before, fixture.Counts()); Assert.Equal(0, fixture.Inbox.PendingCount);
    }

    [Theory(DisplayName = "A New Start Cannot Reserve Or Accept When The Actual Recovery Index Or Host Lifecycle Denies It")]
    [InlineData("unknown")]
    [InlineData("draining")]
    public void RequiresLiveAdmission(string reason)
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source; var before = fixture.Counts();
        if (reason == "unknown") fixture.Index.BlockUnknown("fixture_unknown");
        else fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        var error = Assert.Throws<ExecutionSelectionException>(() => fixture.Service.Start(Guid.NewGuid(), Subject,
            source.Request(new BindingTarget(source.BindingB)), PlanBuilderFixture.CreatedAt));
        Assert.Equal("execution_host_unavailable", error.Message); Assert.Equal(before, fixture.Counts()); Assert.Equal(0, fixture.Inbox.PendingCount);
    }

    [Fact(DisplayName = "Accepted Start Replay Keeps Its Original Plan After Runtime Edits And Missing Current Images Without Duplicating Run Ownership")]
    public void ReplaysOriginalPlan()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source; var operation = Guid.NewGuid();
        var request = source.Request(new BindingTarget(source.BindingB));
        var result = fixture.Service.Start(operation, Subject, request, PlanBuilderFixture.CreatedAt);
        var before = fixture.Counts();
        Assert.False(fixture.Service.Start(operation, Subject, request, PlanBuilderFixture.CreatedAt.AddHours(1)).Acceptance.IsNew);
        Assert.Equal(1, fixture.Inbox.PendingCount);
        using (var owned = fixture.Take(operation))
        {
            var original = owned.Prepared.Total.Plan.Export();
            var catalog = source.Definitions.Read();
            var changes = new NexusPipeline.Core.Automation.AutomationChanges(catalog.Revision, [], [],
                [catalog.Users.Single(user => user.Id == source.UserB) with { Name = "Edited after admission" }], [], [], [], []);
            source.Definitions.Commit(NexusPipeline.Platform.Storage.Automation.AutomationDefinitionStore.Identity(Guid.NewGuid(), Subject, changes), changes, DateTimeOffset.UtcNow);
            Assert.Equal(original, owned.Prepared.Total.Plan.Export());
        }
        File.Delete(source.Image);
        var replay = fixture.Service.Start(operation, Subject, request, PlanBuilderFixture.CreatedAt.AddDays(1));
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(result.JournalId, replay.JournalId); Assert.Equal(result.Plan, replay.Plan);
        Assert.Equal(0, fixture.Inbox.PendingCount); Assert.Equal(before[1..], fixture.Counts()[1..]);
    }

    [Theory(DisplayName = "An Actual Acceptance Transaction Failure Consumes No Operation And Releases Its Unaccepted Resource Reservation")]
    [InlineData("operation_journals")]
    [InlineData("journal_resource_claims")]
    public void ReleasesFailedAcceptance(string table)
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source; var operation = Guid.NewGuid(); var before = fixture.Counts();
        fixture.Execute($"CREATE TRIGGER fixture_admission_failure BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'fixture_admission_failure'); END;");
        var request = source.Request(new BindingTarget(source.BindingB));
        Assert.Throws<SqliteException>(() => fixture.Service.Start(operation, Subject, request, PlanBuilderFixture.CreatedAt));
        Assert.Equal(before, fixture.Counts()); Assert.True(fixture.Index.Current.IsReady); Assert.Empty(fixture.Index.Current.Isolations); Assert.Equal(0, fixture.Inbox.PendingCount);
        fixture.Execute("DROP TRIGGER fixture_admission_failure;");
        Assert.True(fixture.Service.Start(operation, Subject, request, PlanBuilderFixture.CreatedAt).Acceptance.IsNew);
        Assert.Equal(1, fixture.Inbox.PendingCount);
    }

    [Theory(DisplayName = "A Failure After Sql Acceptance Reconciles Only The Unentered Attempt And Publishes Its No Effect Facts Before Releasing Resources")]
    [InlineData(AttemptPersistencePoint.AcceptanceCommitted)]
    [InlineData(AttemptPersistencePoint.JournalCreated)]
    public void ReconcilesUnenteredAttempts(AttemptPersistencePoint point)
    {
        using var fixture = new ExecutionAdmissionFixture(observer: new FailingObserver(point));
        var source = fixture.Source; var operation = Guid.NewGuid(); var request = source.Request(new BindingTarget(source.BindingB));
        Assert.Throws<IOException>(() => fixture.Service.Start(operation, Subject, request, PlanBuilderFixture.CreatedAt));
        Assert.True(fixture.Index.Current.IsReady); Assert.Empty(fixture.Index.Current.Isolations); Assert.Equal(0, fixture.Inbox.PendingCount);
        var parent = Assert.Single(new RecoveryJournalRegistry(source.Database).ReadSnapshot().Journals, item => item.OperationId == operation);
        Assert.Equal(JournalPhase.Committed, parent.Phase);
        var attempt = new AttemptPlanCodec(source.Instance, new(), new(source.Instance, new())).Read(
            NexusPipeline.Platform.Storage.Execution.AttemptPreparedBinding.Reference(source.Instance, parent)).ReadWire();
        var facts = new AttemptReceiptStore(source.Database).Read(Guid.Parse(attempt.AttemptId))!.Facts;
        Assert.Equal(EngineOutcome.NotStarted, facts.Engine); Assert.Equal(BusinessOutcome.Unverified, facts.Business);
        Assert.Equal(LifecycleOutcome.Blocked, facts.Lifecycle); Assert.Equal(RecoveryOutcome.NotRequired, facts.Recovery);
        var replay = fixture.Service.Start(operation, Subject, request, PlanBuilderFixture.CreatedAt);
        Assert.False(replay.Acceptance.IsNew); Assert.Equal("completed", replay.Acceptance.Status);
    }

    [Fact(DisplayName = "Independent Starts Freeze Concurrently But Publish And Transfer Ownership Through One Shared Commit Owner")]
    public async Task SerializesSharedPublication()
    {
        using var observer = new PausingObserver();
        using var fixture = new ExecutionAdmissionFixture(observer: observer); var source = fixture.Source;
        var other = fixture.NewService(); var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        var first = Task.Run(() => fixture.Service.Start(firstId, Subject, source.Request(new BindingTarget(source.BindingB)), PlanBuilderFixture.CreatedAt));
        Task<AttemptAcceptanceResult>? second = null;
        try
        {
            Assert.True(observer.Entered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Run(() => other.Start(secondId, Subject, source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt));
            var deadline = Stopwatch.StartNew();
            while (fixture.Inbox.Commits.Count < 2 && !second.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
            Assert.Equal(2, fixture.Inbox.Commits.Count); Assert.False(second.IsCompleted);
        }
        finally { observer.Release.Set(); }
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(15))).Acceptance.IsNew);
        Assert.True((await second!.WaitAsync(TimeSpan.FromSeconds(15))).Acceptance.IsNew);
        Assert.Equal(2, fixture.Inbox.PendingCount); Assert.True(fixture.Index.Current.IsReady);
        Assert.Equal(2, new RecoveryJournalRegistry(source.Database).ReadSnapshot().Journals.Count(item => item.Kind == JournalKind.RunAttempt));
    }

    [Fact(DisplayName = "A Simultaneous Start For The Same Actual Resources Cannot Enter The Accepted Owners Preparation Or Commit Queue")]
    public async Task RejectsCompetingStarts()
    {
        using var observer = new PausingObserver();
        using var fixture = new ExecutionAdmissionFixture(observer: observer); var source = fixture.Source;
        var request = source.Request(new BindingTarget(source.BindingB)); var firstId = Guid.NewGuid();
        var first = Task.Run(() => fixture.Service.Start(firstId, Subject, request, PlanBuilderFixture.CreatedAt));
        try
        {
            Assert.True(observer.Entered.Wait(TimeSpan.FromSeconds(10)));
            var error = Assert.Throws<ExecutionSelectionException>(() => fixture.Service.Start(Guid.NewGuid(), Subject, request, PlanBuilderFixture.CreatedAt));
            Assert.Equal("execution_resource_conflict", error.Message); Assert.Equal(1, fixture.Inbox.Commits.Count);
        }
        finally { observer.Release.Set(); }
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(15))).Acceptance.IsNew);
        Assert.Equal(1, fixture.Inbox.PendingCount);
    }

    [Fact(DisplayName = "An Observed Unowned Output After Acceptance Cannot Be Reconciled As No Effect Or Automatically Replayed")]
    public void PreservesUnknownOutput()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var service = fixture.NewService(new OutputFaultObserver(source)); var operation = Guid.NewGuid();
        var request = source.Request(new BindingTarget(source.BindingB));
        Assert.Throws<IOException>(() => service.Start(operation, Subject, request, PlanBuilderFixture.CreatedAt));
        var parent = Assert.Single(new RecoveryJournalRegistry(source.Database).ReadSnapshot().Journals, item => item.OperationId == operation);
        Assert.Equal(JournalPhase.Quarantined, parent.Phase); Assert.NotEmpty(fixture.Index.Current.Isolations); Assert.Equal(0, fixture.Inbox.PendingCount);
        var plan = AttemptPreparedBinding.ReadPlan(source.Instance, new(), parent);
        Assert.Null(new AttemptReceiptStore(source.Database).Read(Guid.Parse(plan.AttemptId)));
        Assert.True(Directory.Exists(Path.Combine(source.Instance.RuntimeRoot, plan.OutputDirectory)));
        var replay = service.Start(operation, Subject, request, PlanBuilderFixture.CreatedAt);
        Assert.False(replay.Acceptance.IsNew); Assert.Equal("accepted", replay.Acceptance.Status); Assert.Equal(0, fixture.Inbox.PendingCount);
        Assert.True(fixture.Service.Start(Guid.NewGuid(), Subject, source.Request(new BindingTarget(source.OtherBinding)),
            PlanBuilderFixture.CreatedAt).Acceptance.IsNew);
    }

    [Fact(DisplayName = "The Accepted Run Inbox Cannot Be Shared Across Different Actual Runtime Instances")]
    public void RequiresBoundInbox()
    {
        using var first = new ExecutionAdmissionFixture(); using var second = new ExecutionAdmissionFixture();
        Assert.Throws<IOException>(() => new ExecutionAdmissionService(second.Source.Instance, second.Source.Database, new(), second.Source.Lease,
            second.Resources, second.Index, first.Inbox, new(5, 6, 7)));
        Assert.Throws<IOException>(() => new ExecutionAdmissionService(first.Source.Instance, first.Source.Database, new(), first.Source.Lease,
            first.Resources, second.Index, first.Inbox, new(5, 6, 7)));
        Assert.Equal(0, first.Inbox.PendingCount); Assert.Equal(0, second.Inbox.PendingCount);
    }

    private sealed class OutputFaultObserver(PlanBuilderFixture source) : IAttemptPersistenceObserver
    {
        public void Reached(AttemptPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point != AttemptPersistencePoint.AcceptanceCommitted) return;
            var parent = new RecoveryJournalRegistry(source.Database).ReadSnapshot().Journals.Single(item => item.JournalId == journalId);
            var plan = AttemptPreparedBinding.ReadPlan(source.Instance, new(), parent);
            Directory.CreateDirectory(Path.Combine(source.Instance.RuntimeRoot, plan.OutputDirectory));
            throw new IOException("fixture_unknown_output");
        }
    }

    private sealed class FailingObserver(AttemptPersistencePoint point) : IAttemptPersistenceObserver
    {
        public void Reached(AttemptPersistencePoint actual, Guid journalId, long revision, JournalPhase phase)
        {
            if (actual == point) throw new IOException("fixture_admission_fault");
        }
    }

    private sealed class PausingObserver : IAttemptPersistenceObserver, IDisposable
    {
        private int _entered;
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        public void Reached(AttemptPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point != AttemptPersistencePoint.AcceptanceCommitted || Interlocked.CompareExchange(ref _entered, 1, 0) != 0) return;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("fixture_admission_commit_not_released");
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
