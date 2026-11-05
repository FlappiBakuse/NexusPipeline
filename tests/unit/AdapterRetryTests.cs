using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class AdapterRetryTests
{
    [Fact(DisplayName = "Retry Selection Separates Failed Business Leaves From Explicit Safe Dependency Gates")]
    public void SelectsExplicitGates()
    {
        var selection = AdapterRetryPolicy.Select(Tasks(), Observations(), ["failed"]);
        Assert.Equal(new[] { "failed" }, selection.Targets.ToArray());
        Assert.Equal(new[] { "gate", "failed" }, selection.SelectedIds.ToArray());
        Assert.DoesNotContain("completed", selection.SelectedIds);
        Assert.DoesNotContain("technical", selection.SelectedIds);
    }

    [Theory(DisplayName = "Partial Retry Rejects Unknown Missing And Undeclared Execution Prerequisites")]
    [InlineData("unsafe-gate")]
    [InlineData("missing-gate")]
    [InlineData("undeclared-gate")]
    [InlineData("cleanup-gate")]
    [InlineData("unmapped-enabled")]
    [InlineData("technical-target")]
    [InlineData("successful-target")]
    [InlineData("unknown-target")]
    [InlineData("parent-target")]
    public void RejectsUnsafeProjections(string fault)
    {
        var tasks = Tasks(); var previous = Observations(); var target = "failed";
        switch (fault)
        {
            case "unsafe-gate": tasks = tasks.SetItem(0, tasks[0] with { IsRetrySafe = false }); break;
            case "missing-gate": tasks = tasks.SetItem(0, tasks[0] with { Projection = null }); break;
            case "undeclared-gate": tasks = tasks.SetItem(1, tasks[1] with { RetryPrerequisites = [] }); break;
            case "cleanup-gate": tasks = tasks.SetItem(0, tasks[0] with { WorkflowRole = AdapterWorkflowRole.Cleanup }); break;
            case "unmapped-enabled": tasks = tasks.SetItem(3, tasks[3] with { Projection = null }); break;
            case "technical-target": target = "technical"; previous = previous.SetItem(3, previous[3] with { State = AdapterObservationState.Failed }); break;
            case "successful-target": target = "completed"; break;
            case "unknown-target": previous = previous.SetItem(1, previous[1] with { State = AdapterObservationState.Unknown }); break;
            case "parent-target": tasks = tasks.SetItem(0, tasks[0] with { WorkflowRole = AdapterWorkflowRole.Business }); target = "gate"; break;
        }
        Assert.Throws<ArgumentException>(() => AdapterRetryPolicy.Select(tasks, previous, [target]));
    }

    [Theory(DisplayName = "Retry Carry Forward Preserves Prior Completed Satisfied Inapplicable And Unknown Facts Without Accepting Unexecuted Claims")]
    [InlineData(AdapterObservationState.Succeeded, AdapterCompletion.Completed)]
    [InlineData(AdapterObservationState.Succeeded, AdapterCompletion.Satisfied)]
    [InlineData(AdapterObservationState.Succeeded, AdapterCompletion.Inapplicable)]
    [InlineData(AdapterObservationState.Unknown, AdapterCompletion.NotExecuted)]
    public void PreservesPriorFacts(AdapterObservationState state, AdapterCompletion completion)
    {
        var tasks = Tasks();
        var previous = Observations().SetItem(2, new("completed", state, "prior_fact", completion));
        var selection = AdapterRetryPolicy.Select(tasks, previous, ["failed"]);
        var current = previous.SetItem(1, new("failed", AdapterObservationState.Succeeded, "actual_retry", AdapterCompletion.Completed))
            .SetItem(2, new("completed", AdapterObservationState.Failed, "unexecuted_claim"));
        var merged = AdapterRetryPolicy.Merge(tasks, previous, selection, current);
        Assert.Equal(previous[2], merged[2]);
        Assert.Equal(previous[3], merged[3]);
        Assert.Equal("actual_retry", merged[1].ReasonCode);
        Assert.Equal(AdapterObservationState.Succeeded, merged[1].State);
        Assert.Throws<ArgumentException>(() => AdapterRetryPolicy.Merge(tasks, previous,
            selection with { SelectedIds = selection.SelectedIds.Add("completed") }, current));
    }

    [Fact(DisplayName = "No Proposed Failed Business Tasks Produces No Configuration Replay")]
    public void StopsEmptyRetries()
    {
        var result = AdapterRetryPolicy.Select(Tasks(), Observations(), []);
        Assert.Empty(result.Targets); Assert.Empty(result.SelectedIds);
    }

    [Fact(DisplayName = "Safe Login Is Replayed After Process Closure Even If It Previously Succeeded While Unprojected Cleanup Retains Its Original Configuration")]
    public void RetainsRestartPrerequisites()
    {
        var tasks = Tasks().Add(new("cleanup", "cleanup", true, true, [], true, AdapterWorkflowRole.Cleanup));
        var previous = Observations().SetItem(0, new("gate", AdapterObservationState.Succeeded, "prior_login"))
            .Add(new("cleanup", AdapterObservationState.Unknown, "cleanup_unobserved"));
        var selection = AdapterRetryPolicy.Select(tasks, previous, ["failed"]);
        Assert.Equal(new[] { "gate", "failed" }, selection.SelectedIds);
        Assert.Equal(new[] { "failed" }, selection.Targets);
        Assert.Throws<ArgumentException>(() => AdapterRetryPolicy.Select(tasks.SetItem(4, tasks[4] with { IsRetrySafe = false }), previous, ["failed"]));
    }

    [Theory(DisplayName = "Native Resume Keeps The Complete Frozen Invocation Including Login And Child Scopes And Rejects Incomplete Or Partial Authority")]
    [InlineData("valid")]
    [InlineData("omitted-login")]
    [InlineData("disabled-member")]
    [InlineData("foreign-member")]
    [InlineData("duplicate-member")]
    [InlineData("unknown-enabled")]
    [InlineData("mixed-projection")]
    [InlineData("partial-child")]
    [InlineData("unsafe-owner")]
    [InlineData("technical-owner")]
    [InlineData("successful-owner")]
    [InlineData("inconsistent-owner")]
    public void BindsNativeResume(string fault)
    {
        ImmutableArray<AdapterTask> tasks =
        [new("login", "Login", true, true, [], false, AdapterWorkflowRole.Technical),
         new("daily", "Daily", true, true, ["login"], true) { NativeResumeScope = ["login", "daily", "child", "completed"] },
         new("child", "Child", true, true, ["daily"], false), new("completed", "Completed", true, true, [], false),
         new("disabled", "Disabled", false, false, [], false)];
        ImmutableArray<AdapterTaskObservation> previous =
        [new("login", AdapterObservationState.Succeeded, "prior_login"), new("daily", AdapterObservationState.Failed, "parent_failed"),
         new("child", AdapterObservationState.Unknown, "child_unknown"), new("completed", AdapterObservationState.Succeeded, "prior_completed")];
        ImmutableArray<string> proposed = ["daily"];
        var owner = tasks[1];
        switch (fault)
        {
            case "omitted-login": owner = owner with { NativeResumeScope = ["daily", "child", "completed"] }; break;
            case "disabled-member": owner = owner with { NativeResumeScope = owner.NativeResumeScope.Add("disabled") }; break;
            case "foreign-member": owner = owner with { NativeResumeScope = owner.NativeResumeScope.Add("foreign") }; break;
            case "duplicate-member": owner = owner with { NativeResumeScope = owner.NativeResumeScope.Add("daily") }; break;
            case "unknown-enabled": tasks = tasks.SetItem(4, tasks[4] with { IsEnabled = true }); break;
            case "mixed-projection": tasks = tasks.SetItem(3, tasks[3] with { Projection = Task("completed", []).Projection }); break;
            case "partial-child": previous = previous.SetItem(2, previous[2] with { State = AdapterObservationState.Partial }); break;
            case "unsafe-owner": owner = owner with { IsRetrySafe = false }; break;
            case "technical-owner": owner = owner with { WorkflowRole = AdapterWorkflowRole.Technical }; break;
            case "successful-owner": previous = previous.SetItem(1, previous[1] with { State = AdapterObservationState.Succeeded }); break;
            case "inconsistent-owner":
                tasks = tasks.SetItem(3, tasks[3] with { IsRetrySafe = true, NativeResumeScope = ["completed"] });
                previous = previous.SetItem(3, previous[3] with { State = AdapterObservationState.Failed }); proposed = ["daily", "completed"]; break;
        }
        tasks = tasks.SetItem(1, owner);
        if (fault != "valid") { Assert.Throws<ArgumentException>(() => AdapterRetryPolicy.Select(tasks, previous, proposed)); return; }
        var selection = AdapterRetryPolicy.Select(tasks, previous, proposed);
        Assert.Equal(new[] { "daily" }, selection.Targets);
        Assert.Equal(new[] { "login", "daily", "child", "completed" }, selection.SelectedIds);
        var current = previous.Select(item => item with { State = AdapterObservationState.Succeeded, ReasonCode = "current_native" }).ToImmutableArray();
        Assert.All(AdapterRetryPolicy.Merge(tasks, previous, selection, current), item => Assert.Equal("current_native", item.ReasonCode));
    }

    [Theory(DisplayName = "Proven Unstarted Business Continues After A Failure Through Its Safe Startup Prerequisite While Unknown And Partial Work Remain Blocked")]
    [InlineData("failed-business")]
    [InlineData("failed-startup")]
    [InlineData("no-failure")]
    [InlineData("no-proof")]
    [InlineData("partial")]
    public void ContinuesUnstartedWork(string scenario)
    {
        var tasks = Tasks().Add(Task("pending", ["gate"]) with { RetryPrerequisites = ["gate"] });
        var previous = Observations().Add(new("pending", AdapterObservationState.Unknown, "queue_stopped", AdapterCompletion.NotExecuted));
        if (scenario == "failed-startup") previous = previous.SetItem(1, new("failed", AdapterObservationState.Unknown, "queue_stopped", AdapterCompletion.NotExecuted));
        if (scenario == "no-failure") previous = previous.Select(item => item with { State = AdapterObservationState.Unknown, CompletionKind = AdapterCompletion.NotExecuted }).ToImmutableArray();
        if (scenario == "no-proof") previous = previous.SetItem(4, previous[4] with { CompletionKind = null });
        if (scenario == "partial") previous = previous.SetItem(4, previous[4] with { State = AdapterObservationState.Partial, CompletionKind = null });
        if (scenario is "no-failure" or "no-proof" or "partial")
        { Assert.Throws<ArgumentException>(() => AdapterRetryPolicy.Select(tasks, previous, ["failed", "pending"])); return; }
        var selection = AdapterRetryPolicy.Select(tasks, previous, ["failed", "pending"]);
        Assert.Equal(new[] { "gate", "failed", "pending" }, selection.SelectedIds);
        var current = previous.Select(item => selection.SelectedIds.Contains(item.TaskId)
            ? item with { State = AdapterObservationState.Succeeded, CompletionKind = AdapterCompletion.Completed } : item).ToImmutableArray();
        var merged = AdapterRetryPolicy.Merge(tasks, previous, selection, current);
        Assert.Equal(previous[2], merged[2]);
        Assert.Equal(AdapterObservationState.Succeeded, merged[4].State);
    }

    private static ImmutableArray<AdapterTask> Tasks() =>
    [Task("gate", [], AdapterWorkflowRole.Technical), Task("failed", ["gate"]) with { RetryPrerequisites = ["gate"] },
        Task("completed", []), Task("technical", [], AdapterWorkflowRole.Technical)];
    private static AdapterTask Task(string id, ImmutableArray<string> dependencies, AdapterWorkflowRole role = AdapterWorkflowRole.Business) =>
        new(id, id, true, true, dependencies, true, role) { Projection = new("main", "config.yaml", new([new ConfigurationProperty(id)])) };
    private static ImmutableArray<AdapterTaskObservation> Observations() =>
    [new("gate", AdapterObservationState.Failed, "explicit"), new("failed", AdapterObservationState.Failed, "explicit"),
        new("completed", AdapterObservationState.Succeeded, "completed", AdapterCompletion.Completed),
        new("technical", AdapterObservationState.Unknown, "technical_unknown")];
}
