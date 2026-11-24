using System.Collections.Immutable;

namespace NexusPipeline.Core.Extensions;

public sealed record AdapterRetryTasks(ImmutableArray<string> Targets, ImmutableArray<string> SelectedIds);

public static class AdapterRetryPolicy
{
    public static AdapterRetryTasks Select(ImmutableArray<AdapterTask> tasks, ImmutableArray<AdapterTaskObservation> previous,
        ImmutableArray<string> proposed)
        => Select(AdapterTaskPolicy.Validate(tasks), previous, proposed);

    private static AdapterRetryTasks Select(AdapterTaskPolicy.ValidatedTasks index, ImmutableArray<AdapterTaskObservation> previous,
        ImmutableArray<string> proposed)
    {
        index.RequireResults(previous, proposed, out var targets);
        if (targets.Count == 0) return new([], []);
        var tasks = index.Tasks;
        if (targets.Any(id => !index.Task(id).NativeResumeScope.IsEmpty))
            return SelectNative(tasks, previous, targets);
        // Unprojected cleanup keeps its original lifecycle configuration on every invocation.
        if (tasks.Any(task => task.IsEnabled && task.Projection is null &&
            (task.WorkflowRole != AdapterWorkflowRole.Cleanup || !task.IsRetrySafe))) throw Invalid();
        var prerequisites = tasks.Where(task => task.IsSelected && task.WorkflowRole == AdapterWorkflowRole.Business)
            .SelectMany(task => task.Dependencies).ToHashSet(StringComparer.Ordinal);
        if (targets.Overlaps(prerequisites)) throw Invalid();

        var selected = new HashSet<string>(targets, StringComparer.Ordinal);
        var pending = new Stack<AdapterTask>(proposed.Reverse().Select(index.Task));
        while (pending.TryPop(out var task))
        {
            if (!task.IsSelected || !task.IsEnabled || !task.IsRetrySafe || task.Projection is null ||
                task.WorkflowRole == AdapterWorkflowRole.Cleanup) throw Invalid();
            for (var indexInTask = task.Dependencies.Length - 1; indexInTask >= 0; indexInTask--)
            {
                var dependency = task.Dependencies[indexInTask];
                if (task.RetryPrerequisites.BinarySearch(dependency, StringComparer.Ordinal) < 0) throw Invalid();
                if (selected.Add(dependency)) pending.Push(index.Task(dependency));
            }
        }
        return new(tasks.Where(task => targets.Contains(task.TaskId)).Select(task => task.TaskId).ToImmutableArray(),
            tasks.Where(task => selected.Contains(task.TaskId)).Select(task => task.TaskId).ToImmutableArray());
    }

    private static AdapterRetryTasks SelectNative(ImmutableArray<AdapterTask> tasks,
        ImmutableArray<AdapterTaskObservation> previous, IReadOnlySet<string> targets)
    {
        var scope = tasks.Where(task => task.IsSelected && task.IsEnabled).Select(task => task.TaskId).ToImmutableArray();
        var scopeIds = scope.ToHashSet(StringComparer.Ordinal);
        // Native continuation consumes persisted upstream progress through the unchanged entry and selection.
        if (tasks.Any(task => task.Projection is not null || task.IsEnabled && !task.IsSelected) ||
            previous.Any(item => item.State == AdapterObservationState.Partial) ||
            tasks.Where(task => targets.Contains(task.TaskId)).Any(task =>
                task.NativeResumeScope.Length != scope.Length || !task.NativeResumeScope.All(scopeIds.Contains))) throw Invalid();
        return new(tasks.Where(task => targets.Contains(task.TaskId)).Select(task => task.TaskId).ToImmutableArray(), scope);
    }

    public static ImmutableArray<AdapterTaskObservation> Merge(ImmutableArray<AdapterTask> tasks,
        ImmutableArray<AdapterTaskObservation> previous, AdapterRetryTasks selection, ImmutableArray<AdapterTaskObservation> current)
    {
        if (selection is null) throw Invalid();
        var index = AdapterTaskPolicy.Validate(tasks);
        var expected = Select(index, previous, selection.Targets);
        if (expected.Targets.IsEmpty || !expected.Targets.SequenceEqual(selection.Targets) || !expected.SelectedIds.SequenceEqual(selection.SelectedIds)) throw Invalid();
        var observed = index.RequireResults(current, [], out _);
        var selected = expected.SelectedIds.ToHashSet(StringComparer.Ordinal);
        var result = previous.Select(item => selected.Contains(item.TaskId)
            ? AdapterRepetitionPolicy.Merge(item, observed[item.TaskId]) : item).ToImmutableArray();
        index.RequireResults(result, [], out _);
        return result;
    }

    private static ArgumentException Invalid() => new("adapter_retry_projection_invalid");
}
