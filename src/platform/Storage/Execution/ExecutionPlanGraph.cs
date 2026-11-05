using NexusPipeline.Extensibility.Protocol;
using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionPlanGraph
{
    internal static void Validate(ExecutionPlan plan)
    {
        RequireOrderedDag(plan.Steps.Select(step => (step.StepId, step.DependsOn)));
        var unknownSafety = false;
        foreach (var step in plan.Steps)
        {
            try { _ = AdapterTaskPolicy.Freeze(step.Tasks.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray()); }
            catch (Exception error) when (error is ArgumentException or ConfigurationException)
            { throw new IOException("execution_task_metadata_invalid", error); }
            RequireOrderedDag(step.Tasks.Select(task => (task.TaskId, task.Dependencies)));
            var selected = step.SelectedTaskIds.ToHashSet(StringComparer.Ordinal);
            if (!selected.SetEquals(step.Tasks.Where(task => task.IsSelected).Select(task => task.TaskId)) ||
                step.Tasks.Any(task => selected.Contains(task.TaskId) && (!task.IsEnabled || task.Dependencies.Any(id => !selected.Contains(id)))))
                throw new IOException("execution_selected_tasks_invalid");
            unknownSafety |= step.Tasks.Any(task => selected.Contains(task.TaskId) && task.RetrySafety == "unknown");
        }
        if (unknownSafety && plan.RetryPolicy.Safety != "unknown") throw new IOException("execution_retry_safety_upgraded");
        var created = DateTimeOffset.Parse(plan.CreatedAt, System.Globalization.CultureInfo.InvariantCulture);
        var deadline = DateTimeOffset.Parse(plan.RetryPolicy.DeadlineUtc, System.Globalization.CultureInfo.InvariantCulture);
        if (plan.Budget.TotalTimeoutSeconds == 0 ? deadline != DateTimeOffset.MaxValue :
            deadline <= created || deadline > created.AddSeconds(plan.Budget.TotalTimeoutSeconds))
            throw new IOException("execution_retry_deadline_unbound");
        if (plan.RetryPolicy.MaximumAttempts > 1 && plan.RetryPolicy.Safety != "safe")
            throw new IOException("execution_retry_not_authorized");
    }

    private static void RequireOrderedDag(IEnumerable<(string Id, IReadOnlyList<string> Dependencies)> nodes)
    {
        var prior = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, dependencies) in nodes)
        {
            if (dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Count || dependencies.Any(dependency => !prior.Contains(dependency)) || !prior.Add(id))
                throw new IOException("execution_dependency_order_invalid");
        }
    }
}
