using System.Collections.Immutable;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Extensions;

public static class AdapterBusinessPolicy
{
    public static (BusinessOutcome Outcome, string ReasonCode) Decide(ImmutableArray<AdapterTask> tasks,
        ImmutableArray<AdapterTaskObservation> observations)
    {
        AdapterTaskPolicy.RequireOriginalResults(tasks, observations, []);
        var selected = tasks.Where(task => task.IsSelected && task.WorkflowRole == AdapterWorkflowRole.Business)
            .Select(task => observations.Single(value => value.TaskId == task.TaskId)).ToArray();
        if (selected.Length == 0) return (BusinessOutcome.Unverified, "adapter_business_scope_empty");
        if (selected.Any(value => value.State == AdapterObservationState.Failed))
            return (BusinessOutcome.Failed, "adapter_business_task_failed");
        if (selected.Any(value => value.State == AdapterObservationState.Partial))
            return (BusinessOutcome.Partial, "adapter_business_task_partial");
        if (selected.All(value => value.State == AdapterObservationState.Succeeded))
            return (BusinessOutcome.Succeeded, "adapter_business_tasks_succeeded");
        return (BusinessOutcome.Unverified, "adapter_business_tasks_unverified");
    }
}
