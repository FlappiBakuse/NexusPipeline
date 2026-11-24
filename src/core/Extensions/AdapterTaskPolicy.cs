using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Core.Extensions;

public static class AdapterTaskPolicy
{
    public static ImmutableArray<AdapterTask> Freeze(ImmutableArray<AdapterTask> tasks)
        => Validate(tasks).Tasks;

    internal static ValidatedTasks Validate(ImmutableArray<AdapterTask> tasks)
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
        var byId = new Dictionary<string, AdapterTask>(tasks.Length, StringComparer.Ordinal);
        var selectedCount = 0;
        foreach (var task in tasks)
        {
            var dependencies = task.Dependencies.ToHashSet(StringComparer.Ordinal);
            if (dependencies.Count != task.Dependencies.Length ||
                task.RetryPrerequisites.Distinct(StringComparer.Ordinal).Count() != task.RetryPrerequisites.Length ||
                task.RetryPrerequisites.Any(id => !dependencies.Contains(id)) ||
                task.Dependencies.Any(id => id is null || !byId.TryGetValue(id, out var prior) || task.IsSelected && !prior.IsSelected) ||
                !byId.TryAdd(task.TaskId, task) || task.IsSelected && !task.IsEnabled)
                throw new ArgumentException("adapter_task_graph_invalid");
            if (task.IsSelected) selectedCount++;
        }
        var frozen = tasks.Select(task => task with { Dependencies = task.Dependencies.ToImmutableArray(),
            RetryPrerequisites = task.RetryPrerequisites.Length < 2 ? task.RetryPrerequisites : task.RetryPrerequisites.Order(StringComparer.Ordinal).ToImmutableArray(),
            NativeResumeScope = FreezeResumeScope(task, byId),
            Projection = FreezeProjection(task) }).ToImmutableArray();
        var projections = new HashSet<AdapterTaskProjection>(ProjectionComparer.Instance);
        foreach (var task in frozen)
        {
            if (task.Projection is { } projection && !projections.Add(projection))
                throw new ArgumentException("adapter_task_projection_duplicate");
            byId[task.TaskId] = task;
        }
        return new(frozen, byId, selectedCount);
    }

    private static ImmutableArray<string> FreezeResumeScope(AdapterTask task, IReadOnlyDictionary<string, AdapterTask> tasks)
    {
        if (task.NativeResumeScope.IsEmpty) return [];
        if (!task.IsSelected || !task.IsEnabled || !task.IsRetrySafe || task.WorkflowRole != AdapterWorkflowRole.Business ||
            task.Projection is not null || !task.RetryPrerequisites.IsEmpty ||
            task.NativeResumeScope.Distinct(StringComparer.Ordinal).Count() != task.NativeResumeScope.Length ||
            !task.NativeResumeScope.Contains(task.TaskId, StringComparer.Ordinal) ||
            task.NativeResumeScope.Any(id => id is null || !tasks.TryGetValue(id, out var item) || !item.IsSelected || !item.IsEnabled))
            throw new ArgumentException("adapter_native_resume_scope_invalid");
        return task.NativeResumeScope.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static AdapterTaskProjection? FreezeProjection(AdapterTask task)
    {
        if (task.Projection is not { } projection) return null;
        if (!Identity(projection.ResourceId) || !FileTreePolicy.RelativePath(projection.RelativePath))
            throw new ArgumentException("adapter_task_projection_invalid");
        var selector = ConfigurationPatchPolicy.Freeze(projection.Selector, false);
        if (projection.RepetitionSelector is null) return projection with { Selector = selector };
        var count = ConfigurationPatchPolicy.Freeze(projection.RepetitionSelector, false);
        if (task.RepetitionLimit is null || count.Steps.SequenceEqual(selector.Steps))
            throw new ArgumentException("adapter_repetition_projection_invalid");
        return projection with { Selector = selector, RepetitionSelector = count };
    }

    public static void RequireOriginalResults(ImmutableArray<AdapterTask> frozenTasks,
        ImmutableArray<AdapterTaskObservation> observations, ImmutableArray<string> proposedRetry)
        => Validate(frozenTasks).RequireResults(observations, proposedRetry, out _);

    internal sealed class ValidatedTasks(ImmutableArray<AdapterTask> tasks, IReadOnlyDictionary<string, AdapterTask> byId, int selectedCount)
    {
        internal ImmutableArray<AdapterTask> Tasks => tasks;
        internal AdapterTask Task(string id) => byId[id];

        internal IReadOnlyDictionary<string, AdapterTaskObservation> RequireResults(ImmutableArray<AdapterTaskObservation> observations,
            ImmutableArray<string> proposedRetry, out HashSet<string> proposedIds)
        {
            if (observations.IsDefault || observations.Length != selectedCount || proposedRetry.IsDefault ||
                proposedRetry.Length > selectedCount) throw InvalidResults();
            var observed = new Dictionary<string, AdapterTaskObservation>(observations.Length, StringComparer.Ordinal);
            var failed = false;
            foreach (var item in observations)
            {
                if (item is null || !Identity(item.TaskId) || !Identity(item.ReasonCode) || !Enum.IsDefined(item.State) ||
                    !ValidCompletion(item) || !byId.TryGetValue(item.TaskId, out var task) || !task.IsSelected ||
                    !AdapterRepetitionPolicy.ValidObservation(task, item) || !observed.TryAdd(item.TaskId, item))
                    throw InvalidResults();
                failed |= item.State == AdapterObservationState.Failed;
            }
            proposedIds = new HashSet<string>(proposedRetry.Length, StringComparer.Ordinal);
            foreach (var id in proposedRetry)
            {
                if (id is null || !proposedIds.Add(id) || !observed.TryGetValue(id, out var item)) throw InvalidResults();
                var task = byId[id];
                if (!task.IsEnabled || !task.IsRetrySafe || task.WorkflowRole != AdapterWorkflowRole.Business ||
                    !AdapterRepetitionPolicy.CanRetry(task, item) ||
                    !(item.State == AdapterObservationState.Failed ||
                      item.State == AdapterObservationState.Unknown && item.CompletionKind == AdapterCompletion.NotExecuted && failed))
                    throw InvalidResults();
            }
            return observed;
        }
    }

    private sealed class ProjectionComparer : IEqualityComparer<AdapterTaskProjection>
    {
        internal static readonly ProjectionComparer Instance = new();

        public bool Equals(AdapterTaskProjection? left, AdapterTaskProjection? right)
            => ReferenceEquals(left, right) || left is not null && right is not null &&
                left.ResourceId == right.ResourceId && left.RelativePath == right.RelativePath &&
                left.Selector.Steps.SequenceEqual(right.Selector.Steps);

        public int GetHashCode(AdapterTaskProjection projection)
        {
            var hash = new HashCode();
            hash.Add(projection.ResourceId, StringComparer.Ordinal);
            hash.Add(projection.RelativePath, StringComparer.Ordinal);
            foreach (var step in projection.Selector.Steps) hash.Add(step);
            return hash.ToHashCode();
        }
    }

    private static ArgumentException InvalidResults() => new("adapter_original_task_results_invalid");

    private static bool Identity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && !value.Any(char.IsControl);
    internal static bool ValidCompletion(AdapterTaskObservation item) => item.CompletionKind is null ||
        Enum.IsDefined(item.CompletionKind.Value) && (item.CompletionKind switch
        {
            AdapterCompletion.NotExecuted => item.State == AdapterObservationState.Unknown,
            AdapterCompletion.Satisfied or AdapterCompletion.Inapplicable => item.State == AdapterObservationState.Succeeded,
            _ => true
        });
}
