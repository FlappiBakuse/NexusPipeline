using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public class DailyTaskProtocolTests
{
    [Fact]
    public void SelfManagedPcLaunchBlocksHostLaunchButPreservesEmulatorAndGenericLaunch()
    {
        var script = new NexusPipeline.Modules.Scripts.ScriptInstance { LaunchGame = true, GameMode = "pc" };
        var spec = new NexusPipeline.Modules.Scripts.Contracts.ResolvedScriptSpec(script, "1", new(false, "", "", "", ""), "")
            { SelfManagedPcLaunch = true };
        Assert.False(NexusPipeline.Modules.Execution.ExecutionCoordinator.ShouldHostLaunchGame(script, spec));
        Assert.True(NexusPipeline.Modules.Execution.ExecutionCoordinator.ShouldHostLaunchGame(script, spec with { SelfManagedPcLaunch = false }));
        script.GameMode = "emulator";
        Assert.True(NexusPipeline.Modules.Execution.ExecutionCoordinator.ShouldHostLaunchGame(script, spec));
        script.LaunchGame = false;
        Assert.False(NexusPipeline.Modules.Execution.ExecutionCoordinator.ShouldHostLaunchGame(script, spec));
    }

    private static TaskDefinition Task(string id, string policy = "flow", string? parent = null) => new()
    {
        Id = id, SourceKey = id, Name = id, ParentId = parent, Role = "business", Enabled = true,
        Order = 0, CountsAsUnit = parent is null, RequiredForParent = true, RetryUnitId = parent ?? id,
        RetryRisk = "unsafe", Dependencies = [], Detection = "supported", WorkflowRole = "daily",
        CompletionPolicy = policy, RetryPolicy = new("selective_config", true, []),
        ObservationContract = new("fixture", ["file"], [new("start", "scope_started"),
            new("end", policy == "flow" ? "flow_ended" : "business_succeeded"),
            new("fail", "business_failed"), new("cancel", "upstream_cancelled"), new("skip", "normal_skip")]),
    };

    private static TaskRunReducer Reducer(params TaskDefinition[] tasks)
    {
        var reducer = new TaskRunReducer("run", new("0.2.0", "plan", "run", "fixture", "1", DateTimeOffset.UtcNow,
            "signature", "complete", tasks, []) { SemanticsVersion = "daily-flow-v1" });
        reducer.BeginAttempt("attempt", 1, tasks.Select(t => t.Id));
        return reducer;
    }

    private static void Observe(TaskRunReducer reducer, string id, string status, string kind, string rule, int sequence, int ordinal = 1, string attempt = "attempt")
    {
        reducer.Accept(new TaskObservationBatch
        {
            ProtocolVersion = "0.2.0", Type = "observation", RunId = "run", AttemptId = attempt,
            RunBoundary = "open", BoundaryEvidence = [], Diagnostics = [],
            Observations = [new() { Id = sequence.ToString(), TaskId = id, Status = status, FactKind = kind,
                ExecutionOrdinal = ordinal, ReasonCode = "fixture", SkipKind = status == "skipped" ? "satisfied" : null, Evidence = [new("file", 0, sequence, rule)] }],
        }, new([new("file", 0, sequence, "合成日志")], false));
    }

    [Fact]
    public void AuthoritativeParentProtectsFailedChildrenButFlowParentFails()
    {
        foreach (string policy in new[] { "flow", "authoritative" })
        {
            var reducer = Reducer(Task("parent", policy), Task("child", parent: "parent"));
            Observe(reducer, "parent", "running", "scope_started", "start", 1);
            Observe(reducer, "child", "failed", "business_failed", "fail", 2);
            Observe(reducer, "parent", "succeeded", policy == "flow" ? "flow_ended" : "business_succeeded", "end", 3);
            reducer.FinishAttempt("completed");
            Assert.Equal(policy == "flow" ? "failed" : "partial", reducer.Results.Single(r => r.TaskId == "parent").Status);
            Assert.Equal("failed", reducer.Results.Single(r => r.TaskId == "child").Status);
            Assert.Equal(policy == "flow" ? "selective" : "stop", reducer.SelectRetry(3, false, false).Decision);
            Assert.Equal(policy == "flow" ? "failed" : "partial", reducer.Summarize("completed").Outcome);
        }
    }

    [Fact]
    public void ConsumptionFailureRetriesWithoutChangingRiskAndHonorsBudgetAndCancel()
    {
        var reducer = Reducer(Task("consume"), Task("other"));
        Observe(reducer, "consume", "failed", "business_failed", "fail", 1);
        Observe(reducer, "other", "running", "scope_started", "start", 2);
        Observe(reducer, "other", "succeeded", "flow_ended", "end", 3);
        reducer.FinishAttempt("completed");
        Assert.Equal("failed", reducer.Summarize("completed").Outcome);
        Assert.Equal(new[] { "consume" }, reducer.SelectRetry(3, false, false).IncludedTaskIds);
        Assert.Equal("unsafe", reducer.OriginalPlan.Tasks[0].RetryRisk);
        Assert.Equal("retry.cancelled", reducer.SelectRetry(3, true, false).ReasonCode);
        Assert.Equal("retry.budget_exhausted", reducer.SelectRetry(1, false, false).ReasonCode);
        reducer.BeginAttempt("resume", 2, ["consume", "other"], preserveCompleted: true);
        Observe(reducer, "consume", "skipped", "normal_skip", "skip", 1, attempt: "resume");
        Observe(reducer, "other", "skipped", "normal_skip", "skip", 2, attempt: "resume");
        Assert.Equal("failed", reducer.Results.Single(r => r.TaskId == "consume").Status);
        Assert.Equal("succeeded", reducer.Results.Single(r => r.TaskId == "other").Status);
        Observe(reducer, "consume", "running", "scope_started", "start", 3, attempt: "resume");
        Observe(reducer, "consume", "succeeded", "flow_ended", "end", 4, attempt: "resume");
        Assert.Equal("completed", reducer.Summarize("completed").Outcome);
    }

    [Fact]
    public void MissingBoundariesUseHostEvidenceAndCancelPreservesExistingFacts()
    {
        var reducer = Reducer(Task("started"), Task("missing"));
        Observe(reducer, "started", "running", "scope_started", "start", 1);
        reducer.FinishAttempt("completed");
        Assert.All(reducer.Results, r => Assert.Equal("failed", r.Status));
        Assert.Equal(new[] { "missing_terminal", "missing_logs" }, reducer.HostFailures.Select(e => e.Kind));
        Assert.All(reducer.Results, r => Assert.Single(r.HostEvidenceRefs!));
        var cancelled = Reducer(Task("failed"), Task("active"));
        Observe(cancelled, "failed", "failed", "upstream_cancelled", "cancel", 1);
        Observe(cancelled, "active", "running", "scope_started", "start", 2);
        cancelled.FinishAttempt("cancelled");
        Assert.Equal("failed", cancelled.Results.Single(r => r.TaskId == "failed").Status);
        Assert.Equal("cancelled", cancelled.Results.Single(r => r.TaskId == "active").Status);
        var interrupted = Reducer(Task("parent", "authoritative"), Task("child", parent: "parent"));
        Observe(interrupted, "parent", "succeeded", "business_succeeded", "end", 1);
        Observe(interrupted, "child", "running", "scope_started", "start", 2);
        var report = System.Text.Json.Nodes.JsonNode.Parse(TaskProtocolJson.Write(new
        {
            runId = "run", originalPlan = interrupted.OriginalPlan, finalTaskResults = interrupted.Results,
            attemptReports = new[] { new { attemptId = "attempt", lifecycleOutcome = "running" } },
        }))!.AsObject();
        TaskRunReducer.InterruptDailyReport(report);
        Assert.Equal("failed", report["summary"]!["outcome"]!.GetValue<string>());
        Assert.Equal("process_exit", report["hostEvidence"]![0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void InternalRestartRecoversButConflictingTerminalFails()
    {
        var reducer = Reducer(Task("task"));
        Observe(reducer, "task", "failed", "business_failed", "fail", 1);
        Observe(reducer, "task", "running", "scope_started", "start", 2, 2);
        Observe(reducer, "task", "succeeded", "flow_ended", "end", 3, 2);
        Assert.Equal("succeeded", reducer.Results.Single().Status);
        Assert.False(reducer.Summarize("completed").Recovered);
        Observe(reducer, "task", "failed", "business_failed", "fail", 4, 2);
        Assert.Equal("tasks.conflicting_terminal", reducer.Results.Single().ReasonCode);
    }

    [Fact]
    public void UndeclaredAuthorityRejectsWholeBatchAndHostEvidenceCannotBeForged()
    {
        var reducer = Reducer(Task("task"));
        Assert.Throws<InvalidDataException>(() => Observe(reducer, "task", "succeeded", "business_succeeded", "end", 1));
        Assert.Equal("pending", reducer.Results.Single().Status);
        Assert.Throws<System.Text.Json.JsonException>(() => TaskProtocolJson.Read<TaskObservationBatch>("{\"hostEvidence\":[]}"));
        var unsupported = Task("task") with { Detection = "unsupported" };
        Assert.Throws<InvalidDataException>(() => Reducer(unsupported));
    }

    [Fact]
    public void GapCannotBeOverwrittenByGenericEndAndLegacyPolicyIsUnchanged()
    {
        var reducer = Reducer(Task("task"));
        Observe(reducer, "task", "running", "scope_started", "start", 1);
        reducer.RecordHostFailure("log_gap");
        Observe(reducer, "task", "succeeded", "flow_ended", "end", 2);
        Assert.Equal("failed", reducer.Results.Single().Status);
        var legacy = Task("task") with { CompletionPolicy = null, WorkflowRole = null, ObservationContract = null, RetryPolicy = null };
        var old = new TaskRunReducer("run", new("0.1.0", "plan", "run", "fixture", "1", DateTimeOffset.UtcNow,
            "signature", "complete", [legacy], []));
        old.BeginAttempt("attempt", 1, ["task"]);
        old.FinishAttempt("completed");
        Assert.Equal("unknown", old.Results.Single().Status);
    }
}
