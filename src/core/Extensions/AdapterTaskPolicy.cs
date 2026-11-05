using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Core.Extensions;

public static class AdapterTaskPolicy
{
    public static ImmutableArray<AdapterTask> Freeze(ImmutableArray<AdapterTask> tasks)
    {
        if (tasks.IsDefault || tasks.Length > 4096 || tasks.Any(task => task is null || !Identity(task.TaskId) ||
            string.IsNullOrWhiteSpace(task.Title) || task.Title.Length > 256 || task.TitleLocales is null || task.TitleLocales.Count > 16 ||
            task.TitleLocales.Any(pair => !System.Text.RegularExpressions.Regex.IsMatch(pair.Key,
                "^[a-z]{2,3}(?:-[A-Z][a-z]{3})?(?:-[A-Z]{2}|-[0-9]{3})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
                string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 256) || task.Dependencies.IsDefault || task.Dependencies.Length > 256 ||
            !Enum.IsDefined(task.WorkflowRole) || task.RetryPrerequisites.IsDefault || task.RetryPrerequisites.Length > 256 ||
            task.NativeResumeScope.IsDefault || task.NativeResumeScope.Length > 4096 ||
            task.ExecutionTaskId is not null && !Identity(task.ExecutionTaskId) || !AdapterRepetitionPolicy.ValidTask(task)))
            throw new ArgumentException("adapter_tasks_invalid");
        if (tasks.Select(task => task.ExecutionTaskId ?? task.TaskId).Distinct(StringComparer.Ordinal).Count() != tasks.Length)
            throw new ArgumentException("adapter_execution_tasks_duplicate");
        var prior = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (task.Dependencies.Distinct(StringComparer.Ordinal).Count() != task.Dependencies.Length ||
                task.RetryPrerequisites.Distinct(StringComparer.Ordinal).Count() != task.RetryPrerequisites.Length ||
                task.RetryPrerequisites.Any(id => !task.Dependencies.Contains(id, StringComparer.Ordinal)) ||
                task.Dependencies.Any(id => !prior.Contains(id)) || !prior.Add(task.TaskId) ||
                task.IsSelected && (!task.IsEnabled || task.Dependencies.Any(id => !tasks.Any(other => other.TaskId == id && other.IsSelected))))
                throw new ArgumentException("adapter_task_graph_invalid");
        }
        var frozen = tasks.Select(task => task with { Dependencies = task.Dependencies.ToImmutableArray(),
            RetryPrerequisites = task.RetryPrerequisites.Length < 2 ? task.RetryPrerequisites : task.RetryPrerequisites.Order(StringComparer.Ordinal).ToImmutableArray(),
            NativeResumeScope = FreezeResumeScope(task, tasks),
            Projection = FreezeProjection(task.Projection) }).ToImmutableArray();
        for (var index = 0; index < frozen.Length; index++)
            if (frozen[index].Projection is { } projection && frozen.Take(index).Any(previous => previous.Projection is { } other &&
                other.ResourceId == projection.ResourceId && other.RelativePath == projection.RelativePath &&
                other.Selector.Steps.SequenceEqual(projection.Selector.Steps)))
                throw new ArgumentException("adapter_task_projection_duplicate");
        return frozen;
    }

    private static ImmutableArray<string> FreezeResumeScope(AdapterTask task, ImmutableArray<AdapterTask> tasks)
    {
        if (task.NativeResumeScope.IsEmpty) return [];
        if (!task.IsSelected || !task.IsEnabled || !task.IsRetrySafe || task.WorkflowRole != AdapterWorkflowRole.Business ||
            task.Projection is not null || !task.RetryPrerequisites.IsEmpty ||
            task.NativeResumeScope.Distinct(StringComparer.Ordinal).Count() != task.NativeResumeScope.Length ||
            !task.NativeResumeScope.Contains(task.TaskId, StringComparer.Ordinal) ||
            task.NativeResumeScope.Any(id => !tasks.Any(item => item.TaskId == id && item.IsSelected && item.IsEnabled)))
            throw new ArgumentException("adapter_native_resume_scope_invalid");
        return task.NativeResumeScope.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static AdapterTaskProjection? FreezeProjection(AdapterTaskProjection? projection)
    {
        if (projection is null) return null;
        if (!Identity(projection.ResourceId) || !FileTreePolicy.RelativePath(projection.RelativePath))
            throw new ArgumentException("adapter_task_projection_invalid");
        return projection with { Selector = ConfigurationPatchPolicy.Freeze(projection.Selector, false) };
    }

    public static void RequireOriginalResults(ImmutableArray<AdapterTask> frozenTasks,
        ImmutableArray<AdapterTaskObservation> observations, ImmutableArray<string> proposedRetry)
    {
        frozenTasks = Freeze(frozenTasks);
        if (observations.IsDefault || observations.Any(item => item is null || !Identity(item.TaskId) || !Identity(item.ReasonCode) ||
                !Enum.IsDefined(item.State) || !ValidCompletion(item) ||
                !frozenTasks.Any(task => task.TaskId == item.TaskId && AdapterRepetitionPolicy.ValidObservation(task, item))) ||
            observations.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Count() != observations.Length ||
            !observations.Select(item => item.TaskId).ToHashSet(StringComparer.Ordinal).SetEquals(frozenTasks.Where(task => task.IsSelected).Select(task => task.TaskId)) ||
            proposedRetry.IsDefault || proposedRetry.Distinct(StringComparer.Ordinal).Count() != proposedRetry.Length ||
            proposedRetry.Any(id => !frozenTasks.Any(task => task.TaskId == id && task.IsSelected && task.IsEnabled && task.IsRetrySafe &&
                task.WorkflowRole == AdapterWorkflowRole.Business && observations.Any(item => item.TaskId == id && AdapterRepetitionPolicy.CanRetry(task, item))) ||
                !observations.Any(observation => observation.TaskId == id && (observation.State == AdapterObservationState.Failed ||
                    observation.State == AdapterObservationState.Unknown && observation.CompletionKind == AdapterCompletion.NotExecuted &&
                    observations.Any(previous => previous.State == AdapterObservationState.Failed)))))
            throw new ArgumentException("adapter_original_task_results_invalid");
    }

    private static bool Identity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && !value.Any(char.IsControl);
    internal static bool ValidCompletion(AdapterTaskObservation item) => item.CompletionKind is null ||
        Enum.IsDefined(item.CompletionKind.Value) && (item.CompletionKind switch
        {
            AdapterCompletion.NotExecuted => item.State == AdapterObservationState.Unknown,
            AdapterCompletion.Satisfied or AdapterCompletion.Inapplicable => item.State == AdapterObservationState.Succeeded,
            _ => true
        });
}
