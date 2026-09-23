using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Validation;
using NexusPipeline.Modules.Plugins.Contracts;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class ConfigDiagnosticFeedbackTests
{
    private static TaskPlan Plan(string revision = "r1", string evaluation = "violated", string context = "context") =>
        new("1.2", Guid.NewGuid().ToString("N"), "preview", "unknown-plugin", "0.1.0", DateTimeOffset.UtcNow, "signature", "complete", [], [])
        {
            ConfigAssessment = new("1", [new("target", evaluation, "warning", evaluation == "satisfied" ? "none" : "warn",
                new JsonObject { ["kind"] = "binding" }, [], []) { ReasonText = new JsonObject { ["kind"] = "literal", ["value"] = "Finding" } }]),
            CurrentReadiness = new("attention", false, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), revision, context),
        };

    [Fact]
    public void SameBindingAndRevisionNotifiesOnceButKeepsDiagnosticInResponse()
    {
        var feedback = new ConfigDiagnosticFeedback();
        Assert.True(Assert.Single(feedback.Select(Plan(), "a:s")).ShouldNotify);
        var repeated = Assert.Single(feedback.Select(Plan(), "a:s"));
        Assert.False(repeated.ShouldNotify); Assert.Equal("target", repeated.Check.RuleId);
        Assert.True(Assert.Single(feedback.Select(Plan(), "b:s")).ShouldNotify);
        Assert.True(Assert.Single(feedback.Select(Plan("r2"), "a:s")).ShouldNotify);
        Assert.True(Assert.Single(feedback.Select(Plan("r2", context: "queue-changed"), "a:s")).ShouldNotify);
    }

    [Fact]
    public void RecoveryThenRecurrenceNotifiesAgainWithoutRewritingOriginalFinding()
    {
        var feedback = new ConfigDiagnosticFeedback(); var old = Plan(); string frozen = TaskProtocolJson.Write(old);
        feedback.Select(old, "a:s"); Assert.Empty(feedback.Select(Plan(evaluation: "satisfied"), "a:s"));
        Assert.True(Assert.Single(feedback.Select(Plan(), "a:s")).ShouldNotify);
        Assert.Equal(frozen, TaskProtocolJson.Write(old));
    }
}
