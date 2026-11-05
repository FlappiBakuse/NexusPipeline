using System.Collections.Immutable;

namespace NexusPipeline.Core.Extensions;

public sealed record AdapterRetryTasks(ImmutableArray<string> Targets, ImmutableArray<string> SelectedIds);

public static class AdapterRetryPolicy
{
    public static AdapterRetryTasks Select(ImmutableArray<AdapterTask> tasks, ImmutableArray<AdapterTaskObservation> previous,
        ImmutableArray<string> proposed)
    {
        tasks = AdapterTaskPolicy.Freeze(tasks);
        AdapterTaskPolicy.RequireOriginalResults(tasks, previous, proposed);
        if (proposed.IsEmpty) return new([], []);
        if (tasks.Any(task => proposed.Contains(task.TaskId, StringComparer.Ordinal) && !task.NativeResumeScope.IsEmpty))
            return SelectNative(tasks, previous, proposed);
        // Unprojected cleanup keeps its original lifecycle configuration on every invocation.
        if (tasks.Any(task => task.IsEnabled && task.Projection is null &&
            (task.WorkflowRole != AdapterWorkflowRole.Cleanup || !task.IsRetrySafe))) throw Invalid();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var byId = tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
        foreach (var id in proposed)
        {
            if (tasks.Any(task => task.IsSelected && task.WorkflowRole == AdapterWorkflowRole.Business && task.Dependencies.Contains(id, StringComparer.Ordinal)))
                throw Invalid();
            Include(byId[id]);
        }
        return new(tasks.Where(task => proposed.Contains(task.TaskId, StringComparer.Ordinal)).Select(task => task.TaskId).ToImmutableArray(),
            tasks.Where(task => selected.Contains(task.TaskId)).Select(task => task.TaskId).ToImmutableArray());

        void Include(AdapterTask task)
        {
            if (!task.IsSelected || !task.IsEnabled || !task.IsRetrySafe || task.Projection is null || task.WorkflowRole == AdapterWorkflowRole.Cleanup)
                throw Invalid();
            if (!selected.Add(task.TaskId)) return;
            foreach (var dependency in task.Dependencies)
            {
                if (!task.RetryPrerequisites.Contains(dependency, StringComparer.Ordinal)) throw Invalid();
                Include(byId[dependency]);
            }
        }
    }

    private static AdapterRetryTasks SelectNative(ImmutableArray<AdapterTask> tasks,
        ImmutableArray<AdapterTaskObservation> previous, ImmutableArray<string> proposed)
    {
        var scope = tasks.Where(task => task.IsSelected && task.IsEnabled).Select(task => task.TaskId).ToImmutableArray();
        // Native continuation consumes persisted upstream progress through the unchanged entry and selection.
        if (tasks.Any(task => task.Projection is not null || task.IsEnabled && !task.IsSelected) ||
            previous.Any(item => item.State == AdapterObservationState.Partial) ||
            tasks.Where(task => proposed.Contains(task.TaskId, StringComparer.Ordinal)).Any(task =>
                !task.NativeResumeScope.ToHashSet(StringComparer.Ordinal).SetEquals(scope))) throw Invalid();
        return new(tasks.Where(task => proposed.Contains(task.TaskId, StringComparer.Ordinal)).Select(task => task.TaskId).ToImmutableArray(), scope);
    }

    public static ImmutableArray<AdapterTaskObservation> Merge(ImmutableArray<AdapterTask> tasks,
        ImmutableArray<AdapterTaskObservation> previous, AdapterRetryTasks selection, ImmutableArray<AdapterTaskObservation> current)
    {
        if (selection is null) throw Invalid();
        var expected = Select(tasks, previous, selection.Targets);
        if (expected.Targets.IsEmpty || !expected.Targets.SequenceEqual(selection.Targets) || !expected.SelectedIds.SequenceEqual(selection.SelectedIds)) throw Invalid();
        AdapterTaskPolicy.RequireOriginalResults(tasks, current, []);
        var observed = current.ToDictionary(item => item.TaskId, StringComparer.Ordinal);
        var result = previous.Select(item => expected.SelectedIds.Contains(item.TaskId, StringComparer.Ordinal)
            ? AdapterRepetitionPolicy.Merge(item, observed[item.TaskId]) : item).ToImmutableArray();
        AdapterTaskPolicy.RequireOriginalResults(tasks, result, []);
        return result;
    }

    private static ArgumentException Invalid() => new("adapter_retry_projection_invalid");
}
