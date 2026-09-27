using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Plugin.Abstractions;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class ProviderProgressTests
{
    private static ProviderTaskProjection Projection(int tasks = 1) => new(new("plan", "revision", "authorization", [],
        Enumerable.Range(0, tasks).Select(i => new PluginProviderTask("task" + i, "Task " + i, i)).ToArray(), new()),
        "provider", "1", "record", "user", "script", 1);
    private static PluginProviderEvent Fact(long sequence, string kind, string? status = null, string? task = null) =>
        new(kind, sequence, task, status, new JsonObject());

    [Fact]
    public void TenThousandProgressFactsDoNotSpendTerminalEvidenceBudget()
    {
        var projection = Projection();
        projection.Accept(Fact(1, "ready"));
        projection.Accept(Fact(2, "task_event", "running", "task0"));
        for (long i = 3; i < 10003; i++) projection.Accept(Fact(i, "progress"));
        projection.Accept(Fact(10003, "task_event", "succeeded", "task0"));
        projection.Accept(Fact(10004, "completed", "succeeded"));
        projection.Finish("succeeded", false);
        var report = projection.Snapshot();
        Assert.Equal(4, report["structuredEvidence"]!.AsArray().Count);
        Assert.InRange(report["progressEvidence"]!.AsArray().Count, 1, 256);
        Assert.True(report["diagnosticTruncated"]!.GetValue<bool>());
        Assert.Equal("succeeded", report["engineStatus"]!.GetValue<string>());
        Assert.Equal("unverified", report["businessVerification"]!.GetValue<string>());
        Assert.Equal("unverified", projection.Result("completed").Status);
    }

    [Fact]
    public void MaximumAdmissibleTaskCountRetainsEveryStartAndTerminal()
    {
        var projection = Projection(1024);
        long sequence = 1; projection.Accept(Fact(sequence++, "ready"));
        for (int i = 0; i < 1024; i++)
        {
            projection.Accept(Fact(sequence++, "task_event", "running", "task" + i));
            projection.Accept(Fact(sequence++, "task_event", "succeeded", "task" + i));
        }
        projection.Accept(Fact(sequence, "completed", "succeeded"));
        projection.Finish("succeeded", false);
        Assert.Equal(2050, projection.Snapshot()["structuredEvidence"]!.AsArray().Count);
        Assert.Equal(1024, projection.Snapshot()["finalTaskResults"]!.AsArray().Count);
    }

    [Fact]
    public void ReplayRemainsIdempotentAndConflictsAndGapsAreRejectedAfterProgressEviction()
    {
        var projection = Projection(); var ready = Fact(1, "ready");
        projection.Accept(ready); projection.Accept(ready);
        for (long i = 2; i <= 10002; i++) projection.Accept(Fact(i, "progress"));
        projection.Accept(ready);
        Assert.Throws<InvalidDataException>(() => projection.Accept(Fact(1, "fault", "failed")));
        Assert.Throws<InvalidDataException>(() => projection.Accept(Fact(10004, "progress")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTaskTerminalNeverBecomesSuccess(bool started)
    {
        var projection = Projection(); projection.Accept(Fact(1, "ready"));
        if (started) projection.Accept(Fact(2, "task_event", "running", "task0"));
        Assert.Throws<InvalidDataException>(() => projection.Accept(Fact(started ? 3 : 2, "completed", "succeeded")));
    }

    [Fact]
    public void CancellationRemainsAuthoritativeAfterNativeTerminal()
    {
        var projection = Projection(); projection.Accept(Fact(1, "ready"));
        projection.Accept(Fact(2, "task_event", "running", "task0"));
        projection.Accept(Fact(3, "task_event", "succeeded", "task0"));
        projection.Accept(Fact(4, "cancel_ack", "cancelled"));
        projection.Finish("cancelled", true);
        Assert.Equal("cancelled", projection.Result("cancelled").Status);
        Assert.Throws<InvalidDataException>(() => projection.Accept(Fact(5, "completed", "succeeded")));
    }
}
