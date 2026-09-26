using System.Text.Json.Nodes;
using NexusPipeline.Modules.History;
using Xunit;

namespace NexusPipeline.Tests.History;

public sealed class RunOutcomeProjectorTests
{
    [Fact]
    public void EngineCompletionDoesNotBecomeBusinessVerification()
    {
        var record = new RunRecord { Status = "partial", Attempts = 1, TaskReport = Report("succeeded", "completed", 1, 0, 0, 1) };
        RunOutcomeDimensions result = RunOutcomeProjector.Project(record, configPrepared: false, recoveryFailed: false);
        Assert.Equal("succeeded", result.EngineStatus);
        Assert.Equal("unverified", result.BusinessVerification);
        Assert.Equal("completed", result.ExecutionOutcome);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void RecoveryFailureDoesNotEraseVerifiedBusinessSuccess()
    {
        var record = new RunRecord { Status = "failed", Attempts = 1, TaskReport = Report("succeeded", "completed", 1, 1, 0, 0) };
        RunOutcomeDimensions result = RunOutcomeProjector.Project(record, configPrepared: true, recoveryFailed: true);
        Assert.Equal("verified_succeeded", result.BusinessVerification);
        Assert.Equal("quarantined", result.RecoveryOutcome);
    }

    [Fact]
    public void OldHistoryWithoutDimensionsIsNotBackfilledAsVerified()
    {
        var old = new RunRecord { Status = "success", Attempts = 1 };
        Assert.Null(old.Outcomes);
        Assert.Equal("unverified", RunOutcomeProjector.Project(old, false, false).BusinessVerification);
    }

    private static JsonObject Report(string engine, string execution, int total, int succeeded, int failed, int unknown) =>
        new()
        {
            ["engineStatus"] = engine,
            ["lifecycleOutcome"] = execution,
            ["summary"] = new JsonObject
            {
                ["counts"] = new JsonObject
                {
                    ["total"] = total,
                    ["succeeded"] = succeeded,
                    ["failed"] = failed,
                    ["unknown"] = unknown,
                },
            },
        };
}
