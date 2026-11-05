using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExecutionCancellationTests
{
    [Fact(DisplayName = "An Accepted Cancellation Closes Dispatch Without Erasing The Already Running Root Or Its Real Stop Obligation")]
    public async Task ClosesDispatchOnly()
    {
        using var fixture = new StepWriterFixture(); var root = await fixture.StartAsync();
        var identity = Identity(fixture); var result = Store(fixture).Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt);
        Assert.True(result.Acceptance.IsNew); Assert.Equal("completed", result.Acceptance.Status); Assert.True(result.IsProjected);
        var state = fixture.Scope.VerifyReadOnly(); Assert.True(state.IsDispatchClosed);
        Assert.Equal(JournalPhase.Applying, state.Journal.Phase); Assert.Equal(EffectGateState.Entered, state.Gate.State);
        Assert.Null(Assert.Single(state.Steps).ClosedElapsedMilliseconds); Assert.Equal(1, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_closures")); Assert.Equal(0, fixture.Base.Count("attempt_receipts"));
        Assert.Throws<IOException>(() => fixture.Scope.RequireStart()); Assert.Throws<IOException>(() => fixture.Scope.EnterApplying());
        var before = fixture.Base.Source.Snapshot(); var replay = Store(fixture).Cancel(identity, fixture.JournalId, DateTimeOffset.UtcNow);
        Assert.False(replay.Acceptance.IsNew); AssertSame(before, fixture);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: true);
        Assert.Equal(root.ProcessId, stopped.Closure.Root!.ProcessId); Assert.Equal(root.CreatedAtUtc, stopped.Closure.Root.CreatedAtUtc);
        Assert.Contains(stopped.Closure.Family, member => member == stopped.Closure.Root);
        Assert.Equal(JournalPhase.Applying, fixture.Scope.VerifyReadOnly().Journal.Phase);
    }

    [Fact(DisplayName = "Cancellation Before Effect Entry Keeps The Actual Empty Job And Cannot Use The Old Blocked No Effect Receipt")]
    public async Task KeepsRegisteredJobs()
    {
        using var fixture = new StepWriterFixture(); var source = fixture.Base.Source;
        Store(fixture).Cancel(Identity(fixture), fixture.JournalId, PlanBuilderFixture.CreatedAt);
        Assert.Throws<IOException>(() => fixture.Scope.RequirePrepared()); Assert.Throws<IOException>(() => fixture.Scope.EnterApplying());
        Assert.Throws<IOException>(() => fixture.Writer.Start());
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: false);
        Assert.Empty(stopped.Closure.Family); Assert.Null(stopped.Closure.Root); Assert.False(File.Exists(fixture.Marker + ".output-ready"));
        var session = new PreparedRecoverySession(source.Instance, fixture.JournalId, new(), source.Database, () =>
        {
            var actual = new JournalWriterStore(source.Database).ObserveForCurrentExecutor(fixture.JournalId);
            return new(source.Instance.InstanceId, fixture.JournalId, actual.IsComplete, actual.State);
        });
        session.SealEffectEntry(); var before = source.Snapshot();
        Assert.Throws<IOException>(() => session.CommitNoEffects()); AssertSame(before, fixture);
        Assert.Equal(0, fixture.Base.Count("attempt_receipts"));
        Assert.Equal(JournalPhase.Prepared, fixture.Scope.VerifyReadOnly().Journal.Phase);
    }

    [Theory(DisplayName = "An Interrupted Cancellation Retains Its Accepted Dispatch Barrier And Replays One Physical Close")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaysInterruptedCancellation(bool journalFlushed)
    {
        var point = journalFlushed ? ExecutionCancellationPoint.JournalFlushed : ExecutionCancellationPoint.IntentAccepted;
        using var fixture = new StepWriterFixture(); var source = fixture.Base.Source;
        fixture.Scope.EnterApplying(); var cached = fixture.Scope.VerifyReadOnly(); var identity = Identity(fixture);
        var observer = new CancellationFailure(point);
        Assert.Throws<IOException>(() => Store(fixture, observer).Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt));
        var pending = fixture.Scope.VerifyReadOnly(); Assert.True(pending.IsDispatchClosed); Assert.Equal("accepted", pending.Cancellation!.Status);
        Assert.Equal(cached.Journal.Revision, pending.Journal.Revision);
        Assert.Equal(cached.Latest.Revision + (point == ExecutionCancellationPoint.JournalFlushed ? 1 : 0), pending.Latest.Revision);
        var before = source.Snapshot();
        Assert.Equal("execution_dispatch_closed", Assert.Throws<IOException>(() =>
            new JobClosureStore(source.Instance, source.Database, new()).RequireAttemptStart(cached, Assert.Single(cached.Steps))).Message);
        Assert.Throws<IOException>(() => fixture.Writer.Start()); AssertSame(before, fixture);
        var result = Store(fixture).Cancel(identity, fixture.JournalId, DateTimeOffset.UtcNow);
        Assert.False(result.Acceptance.IsNew); Assert.Equal("completed", result.Acceptance.Status);
        var completed = fixture.Scope.VerifyReadOnly(); Assert.Equal(cached.Journal.Revision + 1, completed.Journal.Revision);
        Assert.Equal(completed.Latest.Revision, completed.Journal.Revision);
        Assert.Single(completed.Latest.Root.GetProperty("participants").EnumerateArray(), DispatchIntentCodec.IsParticipant);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: false);
        Assert.Equal(EffectGateState.Entered, fixture.Scope.VerifyReadOnly().Gate.State);
    }

    [Fact(DisplayName = "A Projection Transaction Failure Leaves The Physical Close And Original Accepted Operation For Exact Retry")]
    public async Task ReplaysFailedProjection()
    {
        using var fixture = new StepWriterFixture(); var identity = Identity(fixture);
        fixture.Base.Execute("CREATE TRIGGER fail_cancel_projection BEFORE UPDATE OF revision ON operation_journals WHEN NEW.journal_id='" +
            fixture.JournalId.ToString("D") + "' BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        long latest;
        try
        {
            Assert.Throws<SqliteException>(() => Store(fixture).Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt));
            var pending = fixture.Scope.VerifyReadOnly(); latest = pending.Latest.Revision;
            Assert.True(latest > pending.Journal.Revision); Assert.Equal("accepted", pending.Cancellation!.Status);
            Assert.Throws<IOException>(() => fixture.Scope.RequirePrepared());
        }
        finally { fixture.Base.Execute("DROP TRIGGER fail_cancel_projection;"); }
        Store(fixture).Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt);
        Assert.Equal(latest, fixture.Scope.VerifyReadOnly().Journal.Revision);
        fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), executed: false);
    }

    [Fact(DisplayName = "The Accepted Intent Rejects Cached Job Registration And Effect Entry Inside Their Original Transactions")]
    public async Task RejectsLateAdmission()
    {
        using var fixture = new StepWriterFixture(); var source = fixture.Base.Source;
        var cached = fixture.Scope.VerifyReadOnly(); var identity = Identity(fixture);
        Assert.Throws<IOException>(() => Store(fixture, new CancellationFailure(ExecutionCancellationPoint.IntentAccepted))
            .Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt));
        using var extra = new OwnedWorkerJob(); var requested = Assert.Single(cached.Steps) with { JobName = extra.Name };
        Assert.Equal(0U, extra.GetAccounting().TotalProcesses); var before = source.Snapshot();
        Assert.Equal("execution_dispatch_closed", Assert.Throws<IOException>(() => source.Database.Write((connection, transaction) =>
        {
            JournalWriterStore.RegisterAttemptJobInside(connection, transaction, cached.Journal, requested); return true;
        })).Message);
        Assert.Equal("execution_dispatch_closed", Assert.Throws<IOException>(() =>
            new JournalEffectGate(source.Database).Enter(cached.Journal, cached.Gate.Revision)).Message);
        Assert.Throws<IOException>(() => Store(fixture).Cancel(Identity(fixture), fixture.JournalId, PlanBuilderFixture.CreatedAt));
        AssertSame(before, fixture); Assert.Equal(1, fixture.Base.Count("journal_writer_jobs"));
        Store(fixture).Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt);
        fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), executed: false);
    }

    [Fact(DisplayName = "A Failed Accepted Intent Transaction Consumes Neither Operation Nor Journal And Preserves Dispatch")]
    public async Task RevertsFailedIntent()
    {
        using var fixture = new StepWriterFixture(); var identity = Identity(fixture);
        fixture.Base.Execute("CREATE TRIGGER fail_cancel_accept BEFORE UPDATE OF initial_journal_id ON operations WHEN NEW.operation_kind='execution.cancel' " +
            "BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        var before = fixture.Base.Source.Snapshot();
        try
        {
            Assert.Throws<SqliteException>(() => Store(fixture).Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt));
            AssertSame(before, fixture); Assert.False(fixture.Scope.VerifyReadOnly().IsDispatchClosed); fixture.Scope.RequirePrepared();
        }
        finally { fixture.Base.Execute("DROP TRIGGER fail_cancel_accept;"); }
        await fixture.StartAsync(); fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), executed: true);
    }

    [Theory(DisplayName = "Conflicting Cancellation Identity Is Rejected Before Any Original Bytes Or Rows Change")]
    [InlineData("subject")]
    [InlineData("parent")]
    [InlineData("parentOperation")]
    [InlineData("run")]
    [InlineData("attempt")]
    [InlineData("kind")]
    [InlineData("freshSubject")]
    public async Task RejectsConflictingIdentity(string field)
    {
        using var fixture = new StepWriterFixture(); using var other = new StepWriterFixture();
        var identity = Identity(fixture); Store(fixture).Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt);
        var state = fixture.Scope.VerifyReadOnly(); var otherState = other.Scope.VerifyReadOnly();
        OperationIdentity request = field switch
        {
            "subject" => Identity(fixture, identity.OperationId, "another-subject"),
            "parent" => Identity(other, identity.OperationId),
            "parentOperation" => ExecutionCancellationStore.Identity(identity.OperationId, identity.SubjectId, fixture.JournalId, Guid.NewGuid(),
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)),
            "run" => ExecutionCancellationStore.Identity(identity.OperationId, identity.SubjectId, fixture.JournalId, state.Journal.OperationId,
                Guid.NewGuid(), Guid.Parse(state.Attempt.AttemptId)),
            "attempt" => ExecutionCancellationStore.Identity(identity.OperationId, identity.SubjectId, fixture.JournalId, state.Journal.OperationId,
                Guid.Parse(state.Attempt.RunId), Guid.NewGuid()),
            "kind" => new(identity.OperationId, identity.SubjectId, identity.RequestDigest, "configuration.save-batch"),
            _ => Identity(fixture, Guid.NewGuid(), "another-subject")
        };
        var before = fixture.Base.Source.Snapshot(); var otherBefore = other.Base.Source.Snapshot();
        Assert.Throws<OperationConflictException>(() => Store(fixture).Cancel(request, field == "parent" ? otherState.Journal.JournalId : fixture.JournalId,
            PlanBuilderFixture.CreatedAt));
        AssertSame(before, fixture); AssertSame(otherBefore, other);
        fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), executed: false);
    }

    [Theory(DisplayName = "Actual Suspended Root And Cancellation Are Serialized By The Same Persistent Root Transaction")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdersSuspendedAdmission(bool rootWins)
    {
        CancellationRootPause? pause = null; StepWriterFixture? owner = null;
        using var fixture = new StepWriterFixture(scopeWrapper: inner => pause = new((StepWriterScope)inner, () => owner!, rootWins));
        owner = fixture;
        using var heldPause = pause!; fixture.Scope.EnterApplying();
        var start = Task.Run(() => fixture.Writer.Start(fixture.Output));
        Assert.True(pause!.Reached.Wait(TimeSpan.FromSeconds(10)));
        var identity = Identity(fixture);
        try
        {
            if (rootWins)
            {
                Assert.Equal(1, fixture.Base.Count("journal_writer_job_roots"));
                Store(fixture).Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt);
            }
            else
            {
                Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
                Assert.Throws<IOException>(() => Store(fixture, new CancellationFailure(ExecutionCancellationPoint.IntentAccepted))
                    .Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt));
            }
        }
        finally { pause.Release.Set(); }
        if (rootWins)
        {
            var root = await start.WaitAsync(TimeSpan.FromSeconds(10));
            var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: true);
            Assert.Equal(root.ProcessId, stopped.Closure.Root!.ProcessId); Assert.Equal(root.CreatedAtUtc, stopped.Closure.Root.CreatedAtUtc);
        }
        else
        {
            Assert.Equal("execution_dispatch_closed", (await Assert.ThrowsAsync<IOException>(async () => { await start.WaitAsync(TimeSpan.FromSeconds(10)); })).Message);
            Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots")); Assert.False(File.Exists(fixture.Marker + ".output-ready"));
            Store(fixture).Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt);
            var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: false);
            Assert.NotNull(stopped.Closure.Root); Assert.Contains(stopped.Closure.Family, member => member == stopped.Closure.Root);
        }
        Assert.Equal(EffectGateState.Entered, fixture.Scope.VerifyReadOnly().Gate.State);
        Assert.Equal(JournalPhase.Applying, fixture.Scope.VerifyReadOnly().Journal.Phase);
    }

    [Theory(DisplayName = "Unknown Or Unbound Physical Cancellation Metadata Is Rejected Read Only")]
    [InlineData("subject")]
    [InlineData("parent")]
    [InlineData("version")]
    [InlineData("reason")]
    public async Task RejectsUnknownMetadata(string field)
    {
        using var fixture = new StepWriterFixture(); Store(fixture).Cancel(Identity(fixture), fixture.JournalId, PlanBuilderFixture.CreatedAt);
        fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), executed: false);
        VerifyReadOnly(fixture);
        var revisions = new JournalStore(fixture.Base.Source.Instance, new()).ReadAllRevisions(fixture.JournalId)
            .Where(item => item.Root.GetProperty("participants").EnumerateArray().Any(DispatchIntentCodec.IsParticipant))
            .ToDictionary(item => JournalPath(fixture, item.Revision), item => item.Export());
        var foreignParent = Guid.NewGuid().ToString("D");
        foreach (var (path, original) in revisions)
        {
            var document = JsonNode.Parse(original)!.AsObject();
            var identity = document["participants"]!.AsArray().Single(item => item!["identity"]!["format"]!.GetValue<string>() == DispatchIntentCodec.Format)!["identity"]!;
            if (field == "subject") identity["subjectId"] = "another-subject";
            else if (field == "parent") identity["parentOperationId"] = foreignParent;
            else if (field == "version") identity["version"] = 2;
            else identity["reasonCode"] = "pretend_completed";
            File.WriteAllText(path, document.ToJsonString());
        }
        var before = fixture.Base.Source.Snapshot();
        try
        {
            if (field == "subject") Assert.Throws<IOException>(() => VerifyReadOnly(fixture));
            else Assert.Throws<JsonException>(() => VerifyReadOnly(fixture));
            AssertSame(before, fixture);
        }
        finally { foreach (var (path, original) in revisions) File.WriteAllBytes(path, original); }
    }

    [Theory(DisplayName = "Accepted Control Metadata Damage Cannot Reopen Dispatch Or Pass Startup Inventory")]
    [InlineData("subject")]
    [InlineData("digest")]
    [InlineData("kind")]
    [InlineData("missingParent")]
    public async Task KeepsDamagedDispatch(string field)
    {
        using var fixture = new StepWriterFixture(); var source = fixture.Base.Source;
        fixture.Scope.EnterApplying(); var cached = fixture.Scope.VerifyReadOnly(); var identity = Identity(fixture);
        Assert.Throws<IOException>(() => Store(fixture, new CancellationFailure(ExecutionCancellationPoint.IntentAccepted))
            .Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt));
        var assignment = field switch { "subject" => "subject_id='another-subject'", "digest" => "request_digest='" + new string('a', 64) + "'",
            "kind" => "operation_kind='unrecognized.cancel'", _ => "initial_journal_id=NULL" };
        fixture.Base.Execute("UPDATE operations SET " + assignment + " WHERE operation_id='" + identity.OperationId.ToString("D") + "';");
        var before = fixture.Base.Source.Snapshot();
        Assert.Throws<IOException>(() => ExecutionCancellationStore.VerifyInventory(source.Instance, source.Database, new()));
        Assert.Throws<IOException>(() => fixture.Scope.RequireStart());
        Assert.Throws<IOException>(() => new JobClosureStore(source.Instance, source.Database, new()).RequireAttemptStart(cached, Assert.Single(cached.Steps)));
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
        AssertSame(before, fixture);
    }

    private static ExecutionCancellationStore Store(StepWriterFixture fixture, IExecutionCancellationObserver? observer = null) =>
        new(fixture.Base.Source.Instance, fixture.Base.Source.Database, new(), fixture.Base.Source.Lease, observer);

    private static OperationIdentity Identity(StepWriterFixture fixture, Guid? operationId = null, string subject = "step-writer-fixture")
    {
        var state = fixture.Scope.VerifyReadOnly();
        return ExecutionCancellationStore.Identity(operationId ?? Guid.NewGuid(), subject, fixture.JournalId, state.Journal.OperationId,
            Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
    }

    private static string JournalPath(StepWriterFixture fixture, long revision) => Path.Combine(fixture.Base.Source.Instance.RuntimeRoot,
        "journals", fixture.JournalId.ToString("D"), revision.ToString("D10") + ".json");

    private static void VerifyReadOnly(StepWriterFixture fixture)
    {
        var source = fixture.Base.Source;
        new StartupJournalRecovery(source.Instance, new(), source.Database, new(Path.Combine(source.Root, "host"), Path.Combine(source.Instance.RuntimeRoot, "extensions")))
            .InspectBindings(new RecoveryJournalRegistry(source.Database).ReadSnapshot());
    }

    private static void AssertSame((string[] Rows, string[] Files) before, StepWriterFixture fixture)
    {
        var after = fixture.Base.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }
}
