using System.Collections.Immutable;

namespace NexusPipeline.Core.Extensions;

public static class AdapterRepetitionPolicy
{
    internal static bool ValidTask(AdapterTask task) => task.RepetitionLimit is null ||
        task.RepetitionLimit is >= 2 and <= 100 && task.WorkflowRole == AdapterWorkflowRole.Business &&
        task.ExecutionTaskId is not null && task.NativeResumeScope.IsEmpty;

    internal static bool ValidObservation(AdapterTask task, AdapterTaskObservation observation) =>
        observation.CompletedRepetitions is null || task.RepetitionLimit is { } limit &&
        observation.CompletedRepetitions >= 0 && observation.CompletedRepetitions <= limit &&
        (observation.State != AdapterObservationState.Succeeded || observation.CompletedRepetitions == limit) &&
        (observation.State != AdapterObservationState.Failed || observation.CompletedRepetitions < limit);

    internal static bool CanRetry(AdapterTask task, AdapterTaskObservation observation) => task.RepetitionLimit is null ||
        observation.CompletedRepetitions is { } completed && completed >= 0 && completed < task.RepetitionLimit;

    public static ImmutableArray<AdapterRepetitionOffset> Offsets(ImmutableArray<AdapterTask> tasks,
        ImmutableArray<AdapterTaskObservation> previous, AdapterRetryTasks selection)
    {
        var expected = AdapterRetryPolicy.Select(tasks, previous, selection.Targets);
        if (!expected.SelectedIds.SequenceEqual(selection.SelectedIds)) throw Invalid();
        return tasks.Where(task => task.RepetitionLimit is not null && selection.SelectedIds.Contains(task.TaskId, StringComparer.Ordinal))
            .Select(task => new AdapterRepetitionOffset(task.TaskId,
                previous.Single(item => item.TaskId == task.TaskId).CompletedRepetitions ?? throw Invalid())).ToImmutableArray();
    }

    internal static AdapterTaskObservation Merge(AdapterTaskObservation previous, AdapterTaskObservation current)
    {
        if (previous.CompletedRepetitions is not { } completed) return current;
        if (current.CompletedRepetitions is { } actual && actual < completed) throw Invalid();
        return current.CompletedRepetitions is null && current.State == AdapterObservationState.Unknown
            ? current with { CompletedRepetitions = completed } : current;
    }

    private static ArgumentException Invalid() => new("adapter_repetition_progress_invalid");
}
