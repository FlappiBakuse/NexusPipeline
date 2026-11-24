using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetryRuntimeTests
{
    [Fact(DisplayName = "Ordinary Invocations And Cold Recovery Share The Frozen Attempt Limit And Stable Business Key")]
    public async Task SharesAttemptLimits()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-invocation-cap",
            retrySafe: true, retryApproved: true, maximumAttempts: 3);
        using var stopping = new CancellationTokenSource();
        var running = fixture.Consumer.RunNextAsync(stopping.Token);
        await AttemptsWritten(fixture.Marker, 2);
        var active = Assert.Single(fixture.Consumer.ReadActive("provider-runtime-fixture"));
        Assert.Equal(1, active.Ordinal);
        Assert.Equal(2, active.Steps[0].InvocationOrdinal);
        Assert.Equal(3, active.Steps[0].MaximumInvocations);
        stopping.Cancel();
        var first = Assert.IsType<AttemptReceipt>(await running);
        Assert.Equal(LifecycleOutcome.Interrupted, first.Facts.Lifecycle);
        Assert.Equal(1, first.Facts.Ordinal); RequireClosure(fixture, first);
        var original = new JournalStore(fixture.Source.Instance, new()).Read(first.JournalId).Export();
        var finality = new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new());
        Assert.False(finality.IsFinal(first));
        var retries = Retry(fixture);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        var continuation = pending.AcceptedRun; Assert.True(continuation.IsActive);
        var accepted = Assert.IsType<AttemptAcceptanceResult>(retries.Start(pending, DateTimeOffset.UtcNow));
        Assert.NotEqual(first.JournalId, accepted.JournalId);
        var second = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(first.Facts.RunId, second.Facts.RunId); Assert.NotEqual(first.Facts.AttemptId, second.Facts.AttemptId);
        Assert.Equal(2, second.Facts.Ordinal); Assert.Equal(EngineOutcome.Failed, second.Facts.Engine);
        Assert.Equal(BusinessOutcome.NotApplicable, second.Facts.Business); RequireClosure(fixture, second);
        Assert.Equal(first, new AttemptReceiptStore(fixture.Source.Database).Read(first.Facts.AttemptId));
        Assert.Equal(original, new JournalStore(fixture.Source.Instance, new()).Read(first.JournalId).Export());
        var attempts = File.ReadAllLines(fixture.Marker + ".attempts").Select(line => line.Split('|')).ToArray();
        Assert.Equal(3, attempts.Length); Assert.Equal(attempts[0][0], attempts[1][0]); Assert.NotEqual(attempts[1][0], attempts[2][0]);
        Assert.Equal(["1", "1", "2"], attempts.Select(item => item[1]));
        Assert.All(attempts, item => Assert.Equal(attempts[0][2], item[2])); Assert.Single(Directory.GetFiles(fixture.Marker + ".effects"));
        var firstState = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), first.JournalId);
        RequireProjectionIsolation(fixture.Source.Instance, firstState);
        var secondState = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), second.JournalId);
        Assert.Equal(2, firstState.AllJobs.Length); Assert.Single(secondState.AllJobs);
        Assert.Equal(2, secondState.PreviousInvocationCount(secondState.Total.Steps[0].StepId));
        Assert.Null(retries.Prepare(second.Facts.RunId, second.Facts.AttemptId, DateTimeOffset.UtcNow));
        Assert.False(finality.IsFinal(first)); Assert.True(finality.IsFinal(second)); Assert.False(continuation.IsActive);
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }

    [Theory(DisplayName = "Either Missing Whole Worker Declaration Or Original Host Approval Prevents Actual Retry")]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RequiresWholeSafety(bool declared, bool approved)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: declared, retryApproved: approved, maximumAttempts: 2);
        var receipt = await fixture.RunInterruptedAsync();
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
        Assert.Null(Retry(fixture).Prepare(receipt.Facts.RunId, receipt.Facts.AttemptId, DateTimeOffset.UtcNow));
        Assert.Equal(0, fixture.Base.Count("execution_retry_intents"));
        Assert.True(new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new()).IsFinal(receipt));
        Assert.Single(File.ReadAllLines(fixture.Marker + ".attempts"));
        if (declared && !approved)
        {
            var state = fixture.State;
            Assert.Equal("safe", state.Total.RetryPolicy.Safety);
            Assert.False(state.Total.RetryPolicy.HostApproved); Assert.Equal(2, state.Total.RetryPolicy.MaximumAttempts);
            var nextAttempt = Guid.NewGuid().ToString("D");
            var unauthorized = state.Attempt with
            {
                OperationId = Guid.NewGuid().ToString("D"), JournalId = Guid.NewGuid().ToString("D"),
                AttemptId = nextAttempt, AttemptOrdinal = 2, StartRequest = null,
                OutputDirectory = $"runs/{state.Attempt.RunId}/{nextAttempt}", LogDirectory = $"logs/{state.Attempt.RunId}/{nextAttempt}"
            };
            var counts = fixture.Base.Counts(); var plans = Plans(fixture);
            var error = Assert.Throws<IOException>(() =>
            {
                using var ignored = new AttemptPlanCodec(fixture.Source.Instance, new(), new(fixture.Source.Instance, new()))
                    .Freeze(unauthorized);
            });
            Assert.Equal("execution_retry_not_authorized", error.Message);
            Assert.Equal(counts, fixture.Base.Counts()); Assert.Equal(plans, Plans(fixture));
            Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        }
    }

    [Fact(DisplayName = "A Cancelled Waiting Run Persists Final Decline And Cannot Dispatch Its Previously Captured Opaque Successor")]
    public async Task KeepsPermanentDecline()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var first = await fixture.RunInterruptedAsync(); var retries = Retry(fixture);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        retries.CancelWaiting(first.Facts.RunId, "provider-runtime-fixture", DateTimeOffset.UtcNow);
        var counts = fixture.Base.Counts(); var before = Plans(fixture);
        var rejected = Assert.Throws<IOException>(() => retries.Start(pending, DateTimeOffset.UtcNow));
        Assert.Equal("execution_retry_budget_continuity_unknown", rejected.Message);
        Assert.Equal(counts, fixture.Base.Counts()); Assert.Equal(before, Plans(fixture));
        var decision = new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new()).ReadDecision(first);
        Assert.True(decision.IsFinal); Assert.Equal("retry_run_cancelled", decision.DeclineReasonCode);
        Assert.Single(File.ReadAllLines(fixture.Marker + ".attempts"));
    }

    [Fact(DisplayName = "An Ordinary Caller Cannot Use The Reserved Next Operation Before Any New Plan Or Journal Is Written")]
    public async Task ReservesRetryOperations()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var first = await fixture.RunInterruptedAsync();
        using var pending = Assert.IsType<PreparedExecutionRetry>(Retry(fixture).Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        var counts = fixture.Base.Counts(); var before = Plans(fixture);
        var rejected = Assert.Throws<IOException>(() => fixture.Service.Start(pending.Frozen.NextOperationId, pending.Intent.SubjectId,
            pending.Request, DateTimeOffset.UtcNow));
        Assert.Equal("execution_retry_source_unbound", rejected.Message);
        Assert.Equal(counts, fixture.Base.Counts()); Assert.Equal(before, Plans(fixture));
    }

    [Fact(DisplayName = "Exact Accepted Replay Uses Its Original References After The Opaque Origin Is Disposed And Provider Images Are Gone")]
    public async Task ReplaysOriginalReferences()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var first = await fixture.RunInterruptedAsync(); var retries = Retry(fixture);
        var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        var accepted = Assert.IsType<AttemptAcceptanceResult>(retries.Start(pending, DateTimeOffset.UtcNow));
        _ = await fixture.Consumer.RunNextAsync(); pending.Dispose();
        await fixture.Scope.DisposeAsync(); File.Delete(fixture.Package.Worker); File.Delete(fixture.Source.OtherImage);
        var counts = fixture.Base.Counts();
        var replay = Assert.IsType<AttemptAcceptanceResult>(retries.Start(pending, DateTimeOffset.UtcNow));
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(accepted.JournalId, replay.JournalId); Assert.Equal(accepted.Plan, replay.Plan);
        Assert.Equal(counts, fixture.Base.Counts());
    }

    [Fact(DisplayName = "A Cold Real Instance Lease Declines Waiting Instead Of Reconstructing The Unmeasured Budget Tail")]
    public async Task DeclinesColdWaiting()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var first = await fixture.RunInterruptedAsync();
        var before = BudgetCount(fixture);
        fixture.Source.Lease.Dispose();
        using var lease = InstanceLease.Acquire(fixture.Source.Instance, new());
        var retries = new ExecutionRetryService(fixture.Source.Instance, fixture.Source.Database, new(), lease, fixture.Service);
        retries.Tick(DateTimeOffset.UtcNow);
        Assert.Equal(before, BudgetCount(fixture)); Assert.Equal(1, fixture.Base.Count("execution_retry_outcomes"));
        var decision = new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new()).ReadDecision(first);
        Assert.True(decision.IsFinal); Assert.Equal("retry_budget_unknown", decision.DeclineReasonCode);
        Assert.Single(File.ReadAllLines(fixture.Marker + ".attempts"));
    }

    [Theory(DisplayName = "Read Only Startup Rejects Changed Retry Metadata Without Repairing The Real Receipt Database")]
    [InlineData("missing")]
    [InlineData("digest")]
    [InlineData("subject")]
    [InlineData("total")]
    public async Task PreservesChangedRetry(string damage)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var first = await fixture.RunInterruptedAsync();
        fixture.Base.Execute(damage switch
        {
            "missing" => "DELETE FROM main.execution_retry_intents;",
            "digest" => "UPDATE main.operations SET request_digest='" + new string('a', 64) + "' WHERE operation_kind='execution.retry.prepare';",
            "subject" => "UPDATE main.execution_retry_intents SET document_json=json_set(document_json,'$.subjectId','foreign');",
            _ => "UPDATE main.execution_retry_intents SET document_json=json_set(document_json,'$.totalPlan.sha256','" + new string('b', 64) + "');"
        });
        var before = RuntimeFingerprint.CaptureDatabaseFiles(fixture.Source.Database.DatabasePath, DatabaseDomain.All);
        var rejected = Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Source.Instance,
            Path.Combine(fixture.Source.Root, "retry-diagnostic"), new JournalStartupAdmission(fixture.Source.Instance, new()), fixture.Source.Lease));
        Assert.Equal("execution_retry_source_unbound", rejected.Message);
        Assert.Equal(before, RuntimeFingerprint.CaptureDatabaseFiles(fixture.Source.Database.DatabasePath, DatabaseDomain.All));
        Assert.Equal(first, new AttemptReceiptStore(fixture.Source.Database).Read(first.Facts.AttemptId));
    }

    private static ExecutionRetryService Retry(ProviderRuntimeFixture fixture) =>
        new(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease, fixture.Service);

    private static long BudgetCount(ProviderRuntimeFixture fixture) => fixture.Base.Count("execution_run_budget_observations");

    [Fact(DisplayName = "Retry Views Keep Ordered Participant Evidence Without Copying The Rest Of The Journal")]
    public void BoundsRetryViewAllocation()
    {
        var participants = new System.Text.Json.Nodes.JsonArray();
        for (var index = 0; index < 8; index++)
        {
            participants.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["identity"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["format"] = WriterIntentCodec.Format, ["stepId"] = "步骤-" + index
                }
            });
            participants.Add(ExecutionInvocationCodec.Participant(new ExecutionInvocation(ExecutionInvocationCodec.Format,
                1, "步骤-" + index, 2, 1, @"Local\NexusWorker-" + Guid.NewGuid().ToString("N"),
                DateTimeOffset.UnixEpoch, []), "script"));
        }
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["participants"] = participants, ["unchanged"] = new string('文', 32 * 1024)
        };
        var options = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var original = System.Text.Json.JsonDocument.Parse(node.ToJsonString(options));
        var root = original.RootElement;
        _ = ExecutionInvocationCodec.Views(root);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var views = ExecutionInvocationCodec.Views(root);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"retry view allocation: {allocated} bytes");
        Assert.True(allocated < 262144, $"Retry views allocated {allocated} bytes");
        Assert.Equal(9, views.Count);
        var evidence = root.GetProperty("participants").EnumerateArray().ToArray();
        for (var index = 0; index < views.Count; index++)
        {
            var actual = views[index].Participants.ToArray();
            Assert.Equal(evidence.Take(index == 8 ? 16 : index * 2 + 1).Select(item => item.GetProperty("identity").GetProperty("stepId").GetString()),
                actual.Select(item => item.GetProperty("identity").GetProperty("stepId").GetString()));
            Assert.Equal(root.GetProperty("unchanged").GetString(), views[index].Root.GetProperty("unchanged").GetString());
        }
    }

    private static string Plans(ProviderRuntimeFixture fixture) => string.Join("\n",
        Directory.GetFiles(Path.Combine(fixture.Source.Instance.RuntimeRoot, "blobs", "plans"))
            .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));

    private static void RequireProjectionIsolation(RuntimeInstance instance, AttemptWriterState state)
    {
        var history = new JournalStore(instance, new()).ReadAllRevisions(state.Journal.JournalId);
        var root = history[^1].Root;
        var participants = root.GetProperty("participants").EnumerateArray().ToArray();
        var step = Assert.Single(state.Total.Steps);
        var dispatch = Assert.Single(participants.Select((item, index) => (Identity: item.GetProperty("identity"), Index: index)),
            item => Format(item.Identity) == "nexus.execution.invocation");
        Assert.Equal(2, dispatch.Identity.GetProperty("ordinal").GetInt32());
        Assert.Equal(step.StepId, dispatch.Identity.GetProperty("stepId").GetString());
        var previousName = dispatch.Identity.GetProperty("previousJobName").GetString();
        var beforeDispatch = Assert.Single(history, item => item.Revision == dispatch.Identity.GetProperty("beforeRevision").GetInt64());
        var beforeIds = participants.Take(dispatch.Index).Select(Id).ToArray();
        Assert.Equal(beforeIds, beforeDispatch.Root.GetProperty("participants").EnumerateArray().Select(Id));
        var allIds = participants.Select(Id).ToArray();
        var registered = participants.Select(item => item.GetProperty("identity")).Where(identity =>
            Format(identity) == "nexus.execution.step-writer" && identity.GetProperty("stage").GetString() == "registered").ToArray();
        Assert.Equal(2, registered.Length);
        Assert.Equal(previousName, registered[0].GetProperty("jobName").GetString());
        var expectedJobs = registered.Select(identity =>
            (Assert.IsType<string>(identity.GetProperty("jobName").GetString()), identity.GetProperty("writerId").GetGuid())).ToArray();
        Assert.NotEqual(expectedJobs[0], expectedJobs[1]);
        using var memo = new AttemptReadMemo(state.Total);
        using var scope = new AttemptReadScope(history, state.Total, memo);
        Assert.Collection(ExecutionInvocationCodec.Views(root),
            prior => Assert.Equal(beforeIds, prior.Participants.Select(Id)),
            current => Assert.Equal(allIds, current.Participants.Select(Id)));
        var old = state with { Latest = beforeDispatch };
        var oldJob = Assert.Single(old.AllJobs);
        Assert.Equal(expectedJobs[0], (oldJob.JobName, oldJob.WriterId));
        Assert.Equal(StepCompletionKind.Collected, Assert.Single(old.Coverage).Kind);
        Assert.Equal(expectedJobs, state.AllJobs.Select(job => (job.JobName, job.WriterId)));
        var currentJob = Assert.Single(state.Jobs);
        Assert.Equal(expectedJobs[1], (currentJob.JobName, currentJob.WriterId));
        Assert.Equal(StepCompletionKind.Collected, Assert.Single(state.Coverage).Kind);
        var damaged = System.Text.Json.Nodes.JsonNode.Parse(history[^1].Export())!.AsObject();
        var participant = damaged["participants"]!.AsArray()[dispatch.Index]!;
        participant["identity"]!["version"] = 999;
        using var foreign = JsonDocument.Parse(damaged.ToJsonString());
        Assert.Equal("execution_invocation_unbound", Assert.Throws<IOException>(() => ExecutionInvocationCodec.Current(foreign.RootElement)).Message);
        scope.Dispose();
        using var following = new AttemptReadScope(history, state.Total, memo);
        Assert.Equal(beforeIds, Assert.Single(ExecutionInvocationCodec.Views(beforeDispatch.Root)).Participants.Select(Id));
        Assert.Equal(expectedJobs, WriterIntentCodec.ReadAllJobs(root, state.Total).Select(job => (job.JobName, job.WriterId)));
        Assert.Equal("execution_invocation_unbound", Assert.Throws<IOException>(() => ExecutionInvocationCodec.Current(foreign.RootElement)).Message);

        static string? Format(JsonElement identity) => identity.TryGetProperty("format", out var value) ? value.GetString() : null;
        static Guid Id(JsonElement participant) => participant.GetProperty("participantId").GetGuid();
    }

    private static async Task AttemptsWritten(string marker, int expected)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            try { if (File.Exists(marker + ".attempts") && File.ReadAllLines(marker + ".attempts").Length >= expected) return; }
            catch (IOException) { }
            await Task.Delay(20);
        }
        throw new TimeoutException("retry_actual_worker_not_ready");
    }

    private static void RequireClosure(ProviderRuntimeFixture fixture, AttemptReceipt receipt)
    {
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), receipt.JournalId);
        foreach (var step in state.AllJobs)
        {
            var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
                .ReadForExecutionEvidence(receipt.JournalId, step.WriterId, step.JobName, step.SessionId);
            Assert.NotNull(closure); Assert.True(closure.WasExecuted); Assert.NotEmpty(closure.Family);
            Assert.All(closure.Family, item => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(item)));
        }
    }
}
