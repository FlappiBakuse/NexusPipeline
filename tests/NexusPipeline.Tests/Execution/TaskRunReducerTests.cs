using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Execution.Monitoring;
using NexusPipeline.Modules.Plugins.Contracts;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class TaskRunReducerTests
{
    private static TaskIncident Incident(string resolution, long sequence = 1) => new()
    {
        Id = "failure", TaskId = "a", ScopeId = "scope-target-a", ExecutionOrdinal = 1,
        Kind = "business_error", Resolution = resolution, ReasonCode = "instance.failed",
        Evidence = resolution == "open" ? [new("stdout", 0, sequence, "failure")]
            : [new("stdout", 0, 1, "failure"), new("stdout", 0, sequence, resolution)],
    };
    private static TaskObservationBatch IncidentBatch(params TaskIncident[] incidents) => new()
    {
        ProtocolVersion = "0.1.0", Type = "observation", RunId = "run", AttemptId = "one",
        Observations = [], Incidents = incidents, RunBoundary = "open", BoundaryEvidence = [], Diagnostics = [],
    };
    private static TaskRunReducer IncidentRun()
    {
        var run = new TaskRunReducer("run", new("0.1.0", "plan", "run", "fake", "1.0.0", DateTimeOffset.UtcNow,
            "signature", "complete", [Task("a"), Task("b")], []));
        run.BeginAttempt("one", 1, ["a", "b"]);
        return run;
    }

    [Fact]
    public void IncidentRecoveryIsIdempotentAndDoesNotInventTaskSuccess()
    {
        var run = IncidentRun();
        var batch = IncidentBatch(Incident("open"), Incident("recovered", 2));
        var logs = new TaskLogBatch([new("stdout", 0, 1, "failure"), new("stdout", 0, 2, "target recovery")], false);
        run.Accept(batch, logs); run.Accept(batch, logs);
        Assert.Equal(new[] { "open", "recovered" }, run.IncidentHistory.Select(e => e.Incident.Resolution));
        Assert.All(run.Results, r => Assert.Equal("pending", r.Status));
        run.FinishAttempt("completed");
        Assert.Equal("unknown", run.Results.Single(r => r.TaskId == "a").Status);
        run.BeginAttempt("two", 2, ["a"]);
        Assert.Equal(2, run.IncidentHistory.Length);
        Assert.Throws<InvalidDataException>(() => run.Accept(batch with { AttemptId = "two", Incidents = [Incident("recovered", 2)] }, logs));
    }

    [Theory]
    [InlineData("task")]
    [InlineData("scope")]
    [InlineData("ordinal")]
    [InlineData("no-new-evidence")]
    public void InvalidIncidentResolutionRejectsWholeBatchBeforeObservationMutation(string change)
    {
        var run = IncidentRun();
        var logs = new TaskLogBatch([new("stdout", 0, 1, "failure"), new("stdout", 0, 2, "unrelated success")], false);
        run.Accept(IncidentBatch(Incident("open")), logs);
        var next = Incident("recovered", 2);
        next = change switch
        {
            "task" => next with { TaskId = "b" },
            "scope" => next with { ScopeId = "other-target" },
            "ordinal" => next with { ExecutionOrdinal = 2 },
            _ => next with { Evidence = Incident("open").Evidence },
        };
        var batch = IncidentBatch(next) with { Observations = [new() { Id = "terminal", TaskId = "a", ExecutionOrdinal = 1,
            Status = "succeeded", ReasonCode = "fixture", Evidence = [new("stdout", 0, 2, "fixture")] }] };
        Assert.Throws<InvalidDataException>(() => run.Accept(batch, logs));
        Assert.Equal("pending", run.Results.Single(r => r.TaskId == "a").Status);
        Assert.Single(run.IncidentHistory);
    }
    private static TaskDefinition Task(string id, string risk = "safe", string role = "business") => new()
    {
        Id = id, Name = id, SourceKey = id, ParentId = null, Role = role, Enabled = true, Order = 0,
        CountsAsUnit = role == "business", RequiredForParent = true, RetryUnitId = id, RetryRisk = risk,
        Dependencies = [], Detection = "supported",
    };
    private static TaskRunReducer Run(params TaskDefinition[] tasks) => new("run", new("0.1.0", "plan", "run", "fake", "1.0.0",
        DateTimeOffset.UtcNow, "signature", "complete", tasks, []));
    private static void Observe(TaskRunReducer reducer, string attempt, params (string Id, string Status)[] facts)
    {
        var logs = facts.Select((_, i) => new TaskLogRecord("stdout", 0, i, "synthetic evidence")).ToArray();
        reducer.Accept(new TaskObservationBatch
        {
            ProtocolVersion = "0.1.0", Type = "observation", RunId = "run", AttemptId = attempt,
            RunBoundary = "open", BoundaryEvidence = [], Diagnostics = [],
            Observations = facts.Select((f, i) => new TaskObservation
            {
                Id = f.Id + f.Status, TaskId = f.Id, ExecutionOrdinal = 1, Status = f.Status, ReasonCode = "fixture",
                Evidence = [new("stdout", 0, i, "fixture")], SkipKind = f.Status == "skipped" ? "satisfied" : null,
            }).ToArray(),
        }, new(logs, false));
    }

    [Theory]
    [InlineData("succeeded", "succeeded", "completed", "ok")]
    [InlineData("failed", "failed", "completed", "bad")]
    [InlineData("succeeded", "failed", "completed", "warn")]
    [InlineData("unknown", "failed", "completed", "warn")]
    [InlineData("unknown", "succeeded", "completed", "muted")]
    [InlineData("succeeded", "succeeded", "interrupted", "bad")]
    [InlineData("skipped", "skipped", "completed", "ok")]
    [InlineData("succeeded", "succeeded", "cancelled", "muted")]
    public void AggregateTruthTable(string a, string b, string lifecycle, string tone)
    {
        var run = Run(Task("a"), Task("b")); run.BeginAttempt("one", 1, ["a", "b"]);
        Observe(run, "one", ("a", a), ("b", b)); run.FinishAttempt(lifecycle);
        Assert.Equal(tone, run.Summarize(lifecycle).Tone);
    }

    [Fact]
    public void RetryKeepsUnselectedSuccessAndRecoversOnlyWithinRun()
    {
        var run = Run(Task("a"), Task("b")); run.BeginAttempt("one", 1, ["a", "b"]);
        Observe(run, "one", ("a", "succeeded"), ("b", "failed")); run.FinishAttempt("completed");
        var retry = run.SelectRetry(2, false, false); Assert.Equal(new[] { "b" }, retry.IncludedTaskIds);
        run.BeginAttempt("two", 2, retry.IncludedTaskIds); Observe(run, "two", ("b", "succeeded")); run.FinishAttempt("completed");
        Assert.True(run.Summarize("completed").Recovered);
        Assert.Equal("ok", run.Summarize("completed").Tone);
        Assert.Equal("muted", Run(Task("a"), Task("b")).Summarize("completed").Tone);
    }

    [Fact]
    public void ReselectedSuccessWithoutLogsBecomesUnknown()
    {
        var run = Run(Task("a")); run.BeginAttempt("one", 1, ["a"]); Observe(run, "one", ("a", "succeeded"));
        run.BeginAttempt("two", 2, ["a"]); run.FinishAttempt("completed");
        Assert.Equal("unknown", Assert.Single(run.Results).Status);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("conditional")]
    [InlineData("unsafe")]
    public void RiskBlocksAutomaticRetry(string risk)
    {
        var run = Run(Task("a", risk)); run.BeginAttempt("one", 1, ["a"]); Observe(run, "one", ("a", "failed"));
        Assert.Equal("retry.risk_not_verified", run.SelectRetry(2, false, false).ReasonCode);
    }

    [Fact]
    public void IndependentUnsafeFailureDoesNotVetoSafeFailure()
    {
        var run = Run(Task("safe"), Task("unsafe", "unknown"), Task("unverified"));
        run.BeginAttempt("one", 1, ["safe", "unsafe", "unverified"]);
        Observe(run, "one", ("safe", "failed"), ("unsafe", "failed"));
        run.FinishAttempt("completed");
        var retry = run.SelectRetry(2, false, false);
        Assert.Equal("selective", retry.Decision);
        Assert.Equal(["safe"], retry.IncludedTaskIds);
        Assert.Equal("unknown", run.Results.Single(r => r.TaskId == "unverified").Status);
    }

    [Fact]
    public void UnsafeMemberOfSharedRetryUnitStillBlocksCandidate()
    {
        var run = Run(Task("a") with { RetryUnitId = "a" }, Task("b", "unknown") with { RetryUnitId = "a" });
        run.BeginAttempt("one", 1, ["a", "b"]);
        Observe(run, "one", ("a", "failed"));
        Assert.Equal("retry.risk_not_verified", run.SelectRetry(2, false, false).ReasonCode);
    }

    [Fact]
    public void TechnicalSuccessDoesNotHideAllBusinessFailure()
    {
        var run = Run(Task("login", role: "technical"), Task("business")); run.BeginAttempt("one", 1, ["login", "business"]);
        Observe(run, "one", ("login", "succeeded"), ("business", "failed"));
        Assert.Equal("bad", run.Summarize("completed").Tone);
        Assert.Equal(1, run.Summarize("completed").Counts["total"]);
        Assert.Equal("muted", Run().Summarize("completed").Tone);
    }

    [Fact]
    public void ParentCompletionCannotFillMissingChildEvidence()
    {
        var run = Run(Task("parent"), Task("child") with { ParentId = "parent", CountsAsUnit = false });
        run.BeginAttempt("one", 1, ["parent", "child"]); Observe(run, "one", ("parent", "succeeded")); run.FinishAttempt("completed");
        Assert.Equal("muted", run.Summarize("completed").Tone);
        Assert.Equal(1, run.Summarize("completed").Counts["total"]);
    }

    [Fact]
    public void ObservationReplayIsIdempotentAndConflictIsUnknown()
    {
        var run = Run(Task("a")); run.BeginAttempt("one", 1, ["a"]);
        Observe(run, "one", ("a", "succeeded")); Observe(run, "one", ("a", "succeeded"));
        Assert.Equal("ok", run.Summarize("completed").Tone);
        Observe(run, "one", ("a", "failed"));
        Assert.Equal("unknown", Assert.Single(run.Results).Status);
    }

    [Fact]
    public void LogBufferDoesNotAdvanceBeforeAcknowledgmentOrMergePartialLines()
    {
        var buffer = new TaskLogBuffer(); buffer.Append("stdout", "中");
        buffer.Append("stdout", "文\r\nnext"); var batch = buffer.Peek();
        Assert.Equal("中文", Assert.Single(batch.Records).Text);
        buffer.Append("stdout", " line\n"); Assert.Equal(TaskProtocolJson.Write(batch), TaskProtocolJson.Write(buffer.Peek()));
        buffer.Acknowledge(batch); Assert.Equal("next line", Assert.Single(buffer.Peek().Records).Text);
    }
}
