using System.Collections.Immutable;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Execution;
using Xunit;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.CoreTests;

public sealed class AdapterTaskTests
{
    [Fact(DisplayName = "Execution Occurrence Aliases Are Preserved And Cannot Collapse Distinct Business Tasks")]
    public void BindsExecutionOccurrences()
    {
        var mapped = Original.SetItem(0, Original[0] with { ExecutionTaskId = "native-occurrence" });
        Assert.Equal("native-occurrence", AdapterTaskPolicy.Freeze(mapped)[0].ExecutionTaskId);
        foreach (var invalid in new[] { "", "line\nbreak", new string('x', 121), "unsafe" })
            Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(mapped.SetItem(0, mapped[0] with { ExecutionTaskId = invalid })));
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(mapped.SetItem(1, mapped[1] with { ExecutionTaskId = "native-occurrence" })));
    }

    private static readonly ImmutableArray<AdapterTask> Original =
    [new("mail", "Mail", true, true, [], true), new("unsafe", "Purchase", true, true, ["mail"], false),
        new("disabled", "Disabled", false, false, [], true)];

    [Fact(DisplayName = "Only Original Selected Enabled Safe Tasks With An Actual Failure Can Be Proposed")]
    public void ProposesSafeRetries()
    {
        ImmutableArray<AdapterTaskObservation> observations =
        [new("mail", AdapterObservationState.Failed, "explicit"), new("unsafe", AdapterObservationState.Failed, "explicit")];
        AdapterTaskPolicy.RequireOriginalResults(Original, observations, ["mail"]);
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(Original, observations, ["unsafe"]));
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(Original, observations, ["disabled"]));
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(Original, observations.SetItem(0,
            observations[0] with { State = AdapterObservationState.Unknown }), ["mail"]));
    }

    [Fact(DisplayName = "Observation Facts Cannot Add Omit Duplicate Or Invent States And Reason Identity")]
    public void RejectsForgedObservations()
    {
        ImmutableArray<AdapterTaskObservation> observations =
        [new("mail", AdapterObservationState.Failed, "explicit"), new("unsafe", AdapterObservationState.Unknown, "unproven")];
        foreach (var changed in new[] { observations.RemoveAt(1), observations.Add(observations[0]),
            observations.SetItem(1, new("other", AdapterObservationState.Unknown, "unproven")),
            observations.SetItem(0, observations[0] with { State = (AdapterObservationState)999 }),
            observations.SetItem(0, observations[0] with { ReasonCode = "line\nbreak" }) })
            Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(Original, changed, []));
    }

    [Theory(DisplayName = "A Safe Flag Cannot Turn Technical Or Cleanup Work Into A Business Retry Target")]
    [InlineData(AdapterWorkflowRole.Business, true)]
    [InlineData(AdapterWorkflowRole.Technical, false)]
    [InlineData(AdapterWorkflowRole.Cleanup, false)]
    public void RequiresBusinessRetry(AdapterWorkflowRole role, bool allowed)
    {
        ImmutableArray<AdapterTask> tasks = [new("target", "Target", true, true, [], true, role)];
        ImmutableArray<AdapterTaskObservation> observations = [new("target", AdapterObservationState.Failed, "explicit")];
        AdapterTaskPolicy.RequireOriginalResults(tasks, observations, []);
        if (allowed) AdapterTaskPolicy.RequireOriginalResults(tasks, observations, ["target"]);
        else Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(tasks, observations, ["target"]));
    }

    [Fact(DisplayName = "Frozen Task Selection Requires The Original Ordered Enabled Dependency Closure")]
    public void RequiresDependencyClosure()
    {
        Assert.Equal(Original.ToArray(), AdapterTaskPolicy.Freeze(Original).ToArray());
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(Original.SetItem(0, Original[0] with { IsSelected = false })));
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(Original.SetItem(0, Original[0] with { Dependencies = ["unsafe"] })));
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(Original.Add(Original[0])));
    }

    [Theory(DisplayName = "Business Outcome Uses Only Selected Business Tasks And Preserves Explicit Failures")]
    [InlineData(AdapterObservationState.Succeeded, BusinessOutcome.Succeeded)]
    [InlineData(AdapterObservationState.Partial, BusinessOutcome.Partial)]
    [InlineData(AdapterObservationState.Failed, BusinessOutcome.Failed)]
    [InlineData(AdapterObservationState.Unknown, BusinessOutcome.Unverified)]
    public void CountsBusinessTasks(AdapterObservationState state, BusinessOutcome expected)
    {
        ImmutableArray<AdapterTask> tasks = [new("prepare", "Prepare", true, true, [], false, AdapterWorkflowRole.Technical),
            new("mail", "Mail", true, true, ["prepare"], true), new("cleanup", "Cleanup", true, true, [], false, AdapterWorkflowRole.Cleanup)];
        ImmutableArray<AdapterTaskObservation> observations = [new("prepare", AdapterObservationState.Unknown, "no_business_fact"),
            new("mail", state, "actual_business_fact"), new("cleanup", AdapterObservationState.Unknown, "no_business_fact")];
        Assert.Equal(expected, AdapterBusinessPolicy.Decide(tasks, observations).Outcome);
        Assert.Equal(BusinessOutcome.Unverified, AdapterBusinessPolicy.Decide(tasks.RemoveAt(1), observations.RemoveAt(1)).Outcome);
    }

    [Theory]
    [InlineData("adapter_task_missing_logs", null, EngineOutcome.Succeeded, BusinessOutcome.Failed, AdapterObservationState.Failed)]
    [InlineData("adapter_task_missing_terminal", null, EngineOutcome.Succeeded, BusinessOutcome.Failed, AdapterObservationState.Failed)]
    [InlineData("adapter_task_opaque", null, EngineOutcome.Succeeded, BusinessOutcome.Succeeded, AdapterObservationState.Unknown)]
    [InlineData("adapter_task_opaque", null, EngineOutcome.Failed, BusinessOutcome.Unverified, AdapterObservationState.Unknown)]
    [InlineData("adapter_task_opaque", null, EngineOutcome.Unknown, BusinessOutcome.Unverified, AdapterObservationState.Unknown)]
    [InlineData("adapter_observation_unavailable", null, EngineOutcome.Succeeded, BusinessOutcome.Failed, AdapterObservationState.Failed)]
    [InlineData("adapter_log_gap", null, EngineOutcome.Succeeded, BusinessOutcome.Failed, AdapterObservationState.Failed)]
    [InlineData("skipped", AdapterCompletion.Inapplicable, EngineOutcome.Succeeded, BusinessOutcome.Unverified, AdapterObservationState.Succeeded)]
    [InlineData("satisfied", AdapterCompletion.Satisfied, EngineOutcome.Succeeded, BusinessOutcome.Unverified, AdapterObservationState.Succeeded)]
    [InlineData("not_executed", AdapterCompletion.NotExecuted, EngineOutcome.Succeeded, BusinessOutcome.Unverified, AdapterObservationState.Unknown)]
    public void ClosesDailyScope(string reason, AdapterCompletion? completion, EngineOutcome engine,
        BusinessOutcome expected, AdapterObservationState expectedState)
    {
        var task = Original[0];
        var raw = new AdapterTaskObservation(task.TaskId,
            completion is AdapterCompletion.Inapplicable or AdapterCompletion.Satisfied ? AdapterObservationState.Succeeded : AdapterObservationState.Unknown, reason, completion);
        var result = AdapterBusinessPolicy.DecideDaily([task], [raw], engine, true, false);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expectedState, Assert.Single(result.Tasks).State);
        if (raw.State == AdapterObservationState.Unknown)
            Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults([task], [raw], [task.TaskId]));
    }

    [Fact]
    public void ProtectsAuthoritativeParent()
    {
        ImmutableArray<AdapterTask> tasks = [Original[0], Original[1]];
        ImmutableArray<AdapterTaskObservation> observations = [
            new("mail", AdapterObservationState.Partial, "okww_parent_completed_child_failure_retry_forbidden"),
            new("unsafe", AdapterObservationState.Failed, "child_failure")];
        Assert.Equal(BusinessOutcome.Partial, AdapterBusinessPolicy.DecideDaily(tasks, observations, EngineOutcome.Succeeded, true, false).Outcome);
        var independent = tasks.SetItem(1, tasks[1] with { Dependencies = [] });
        Assert.Equal(BusinessOutcome.Failed, AdapterBusinessPolicy.DecideDaily(independent, observations, EngineOutcome.Succeeded, true, false).Outcome);
        var missing = observations.SetItem(1, observations[1] with { State = AdapterObservationState.Unknown, ReasonCode = "adapter_task_missing_logs" });
        Assert.Equal(BusinessOutcome.Failed, AdapterBusinessPolicy.DecideDaily(tasks, missing, EngineOutcome.Succeeded, true, false).Outcome);
    }

    [Theory(DisplayName = "Normal Completion Metadata Must Agree With The Frozen Observation State")]
    [InlineData(AdapterCompletion.Satisfied, AdapterObservationState.Succeeded, true)]
    [InlineData(AdapterCompletion.Inapplicable, AdapterObservationState.Succeeded, true)]
    [InlineData(AdapterCompletion.NotExecuted, AdapterObservationState.Unknown, true)]
    [InlineData(AdapterCompletion.Satisfied, AdapterObservationState.Failed, false)]
    [InlineData(AdapterCompletion.NotExecuted, AdapterObservationState.Succeeded, false)]
    public void ValidatesCompletionKinds(AdapterCompletion completion, AdapterObservationState state, bool valid)
    {
        ImmutableArray<AdapterTaskObservation> observations = [new("mail", state, "observed", completion),
            new("unsafe", AdapterObservationState.Unknown, "unproven")];
        if (valid) AdapterTaskPolicy.RequireOriginalResults(Original, observations, []);
        else Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(Original, observations, []));
    }
}

public sealed class TaskProjectionTests
{
    [Fact(DisplayName = "Frozen Task Projection Keeps Exact Resource Path And Selector With Explicit Retry Prerequisites")]
    public void FreezesProjectionBindings()
    {
        var tasks = Tasks();
        var frozen = AdapterTaskPolicy.Freeze(tasks);
        var leaf = frozen[1];
        Assert.Equal("main", leaf.Projection!.ResourceId);
        Assert.Equal("config.yaml", leaf.Projection.RelativePath);
        Assert.Equal(new ConfigurationProperty("leaf_enable"), Assert.Single(leaf.Projection.Selector.Steps));
        Assert.Equal(new[] { "parent" }, leaf.RetryPrerequisites.ToArray());
        Assert.Equal(tasks[0].IsRetrySafe, frozen[0].IsRetrySafe);
    }

    [Theory(DisplayName = "Task Projection Cannot Introduce Foreign Dependencies Duplicate Targets Or Unsafe Paths")]
    [InlineData("resource")]
    [InlineData("path")]
    [InlineData("selector")]
    [InlineData("alias")]
    [InlineData("prerequisite")]
    [InlineData("duplicate")]
    [InlineData("default")]
    public void RejectsProjectionForgery(string fault)
    {
        var tasks = Tasks(); var leaf = tasks[1];
        leaf = fault switch
        {
            "resource" => leaf with { Projection = leaf.Projection! with { ResourceId = "" } },
            "path" => leaf with { Projection = leaf.Projection! with { RelativePath = "../config.yaml" } },
            "selector" => leaf with { Projection = leaf.Projection! with { Selector = new([]) } },
            "alias" => leaf with { Projection = tasks[0].Projection! with { Selector = new([new ConfigurationProperty("parent_enable")]) } },
            "prerequisite" => leaf with { RetryPrerequisites = ["unknown"] },
            "duplicate" => leaf with { RetryPrerequisites = ["parent", "parent"] },
            _ => leaf with { RetryPrerequisites = default }
        };
        if (fault == "selector") Assert.Throws<ConfigurationException>(() => AdapterTaskPolicy.Freeze(tasks.SetItem(1, leaf)));
        else Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(tasks.SetItem(1, leaf)));
    }

    private static ImmutableArray<AdapterTask> Tasks() =>
    [new("parent", "Parent", true, true, [], false) { Projection = Projection("parent_enable") },
        new("leaf", "Leaf", true, true, ["parent"], true) { Projection = Projection("leaf_enable"), RetryPrerequisites = ["parent"] }];
    private static AdapterTaskProjection Projection(string key) => new("main", "config.yaml", new([new ConfigurationProperty(key)]));
}
