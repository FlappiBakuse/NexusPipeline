using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class AdapterRepetitionTests
{
    [Fact(DisplayName = "Retry Replays Login But Carries Completed Business Repetitions Without Repeating Successful Tasks")]
    public void ContinuesRemainingCount()
    {
        var tasks = Tasks(); var previous = Previous();
        var selection = AdapterRetryPolicy.Select(tasks, previous, ["finite", "pending"]);
        Assert.Equal(new[] { "login", "finite", "pending" }, selection.SelectedIds);
        Assert.Equal(new AdapterRepetitionOffset("finite", 1), Assert.Single(AdapterRepetitionPolicy.Offsets(tasks, previous, selection)));
        var current = previous.SetItem(0, new("login", AdapterObservationState.Succeeded, "login_replayed"))
            .SetItem(1, new("done", AdapterObservationState.Unknown, "not_selected"))
            .SetItem(2, new("finite", AdapterObservationState.Succeeded, "remaining_completed", AdapterCompletion.Completed) { CompletedRepetitions = 3 })
            .SetItem(3, new("pending", AdapterObservationState.Succeeded, "completed", AdapterCompletion.Completed));
        var merged = AdapterRetryPolicy.Merge(tasks, previous, selection, current);
        Assert.Equal(previous[1], merged[1]);
        Assert.Equal(3, merged[2].CompletedRepetitions);
        Assert.Throws<ArgumentException>(() => AdapterRetryPolicy.Select(tasks, merged, ["finite"]));
    }

    [Theory(DisplayName = "Finite Retry Rejects Missing Invalid Exhausted And Regressing Completed Counts")]
    [InlineData("missing")]
    [InlineData("negative")]
    [InlineData("exhausted")]
    [InlineData("exceeded")]
    [InlineData("success-before-limit")]
    [InlineData("regression")]
    public void RejectsUnprovenProgress(string fault)
    {
        var tasks = Tasks(); var previous = Previous();
        if (fault == "regression")
        {
            var selected = AdapterRetryPolicy.Select(tasks, previous, ["finite"]);
            var current = previous.SetItem(2, previous[2] with { CompletedRepetitions = 0 });
            Assert.Throws<ArgumentException>(() => AdapterRetryPolicy.Merge(tasks, previous, selected, current));
            return;
        }
        var count = fault switch { "missing" => (int?)null, "negative" => -1, "exhausted" => 3, "exceeded" => 4, _ => 2 };
        previous = previous.SetItem(2, previous[2] with { CompletedRepetitions = count,
            State = fault == "success-before-limit" ? AdapterObservationState.Succeeded : AdapterObservationState.Failed });
        Assert.Throws<ArgumentException>(() => AdapterRetryPolicy.Select(tasks, previous, ["finite"]));
    }

    [Fact(DisplayName = "Unobserved Retry Preserves Prior Count Without Authorizing Unknown Work To Replay")]
    public void RetainsUnknownProgress()
    {
        var tasks = Tasks(); var previous = Previous();
        var selected = AdapterRetryPolicy.Select(tasks, previous, ["finite"]);
        var current = previous.SetItem(2, new("finite", AdapterObservationState.Unknown, "output_incomplete"));
        var merged = AdapterRetryPolicy.Merge(tasks, previous, selected, current);
        Assert.Equal(1, merged[2].CompletedRepetitions);
        Assert.Throws<ArgumentException>(() => AdapterRetryPolicy.Select(tasks, merged, ["finite"]));
    }

    [Theory(DisplayName = "Finite Count Authority Requires A Frozen Business Execution Occurrence")]
    [InlineData("technical")]
    [InlineData("unmapped")]
    [InlineData("limit")]
    public void RequiresFrozenOccurrence(string fault)
    {
        var tasks = Tasks(); var task = tasks[2];
        task = fault switch { "technical" => task with { WorkflowRole = AdapterWorkflowRole.Technical },
            "unmapped" => task with { ExecutionTaskId = null }, _ => task with { RepetitionLimit = 101 } };
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(tasks.SetItem(2, task)));
    }

    private static ImmutableArray<AdapterTask> Tasks() =>
    [Task("login") with { WorkflowRole = AdapterWorkflowRole.Technical }, Task("done"),
        Task("finite") with { RepetitionLimit = 3, Dependencies = ["login"], RetryPrerequisites = ["login"] }, Task("pending")];
    private static AdapterTask Task(string id) => new(id, id, true, true, [], true)
    { ExecutionTaskId = "execution-" + id, Projection = new("main", "", new([new ConfigurationProperty(id)])) };
    private static ImmutableArray<AdapterTaskObservation> Previous() =>
    [new("login", AdapterObservationState.Succeeded, "login_completed"), new("done", AdapterObservationState.Succeeded, "prior_completed"),
        new("finite", AdapterObservationState.Failed, "second_failed") { CompletedRepetitions = 1 },
        new("pending", AdapterObservationState.Unknown, "queue_stopped", AdapterCompletion.NotExecuted)];
}
