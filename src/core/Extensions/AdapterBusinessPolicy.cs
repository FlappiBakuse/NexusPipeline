using System.Collections.Immutable;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Extensions;

public static class AdapterBusinessPolicy
{
    public const string OpaqueTask = "adapter_task_opaque";

    public static (BusinessOutcome Outcome, string ReasonCode, ImmutableArray<AdapterTaskObservation> Tasks) DecideDaily(
        ImmutableArray<AdapterTask> tasks, ImmutableArray<AdapterTaskObservation> observations,
        EngineOutcome engine, bool naturalCompletion, bool hasUnselectedOpaque)
    {
        AdapterTaskPolicy.RequireOriginalResults(tasks, observations, []);
        var effective = observations.Select(value => ReduceDaily(value, engine, naturalCompletion)).ToImmutableArray();
        var business = tasks.Where(task => task.IsSelected && task.WorkflowRole == AdapterWorkflowRole.Business)
            .Select(task => effective.Single(value => value.TaskId == task.TaskId)).ToArray();
        var opaque = hasUnselectedOpaque || business.Any(value => value.ReasonCode == OpaqueTask);
        var known = business.Where(value => value.ReasonCode != OpaqueTask).ToArray();
        if (known.Any(value => value.State == AdapterObservationState.Failed &&
            (!ProtectedChild(tasks, known, value.TaskId) || !observations.Any(raw => raw.TaskId == value.TaskId && raw.State == AdapterObservationState.Failed))))
            return (BusinessOutcome.Failed, "adapter_business_task_failed", effective);
        if (engine == EngineOutcome.Failed && known.Length > 0)
            return (BusinessOutcome.Failed, "execution_engine_failed", effective);
        if (known.Any(value => value.State == AdapterObservationState.Partial))
            return (BusinessOutcome.Partial, "adapter_business_task_partial", effective);
        if (engine == EngineOutcome.Succeeded && naturalCompletion && known.All(value => value.State == AdapterObservationState.Succeeded) &&
            (known.Any(value => value.CompletionKind is null or AdapterCompletion.Completed) || known.Length == 0 && opaque))
            return (BusinessOutcome.Succeeded, opaque ? "adapter_business_verified_scope_succeeded" : "adapter_business_tasks_succeeded", effective);
        return (BusinessOutcome.Unverified, business.Length == 0 && !opaque ? "adapter_business_scope_empty" : "adapter_business_tasks_unverified", effective);
    }

    private static AdapterTaskObservation ReduceDaily(AdapterTaskObservation raw, EngineOutcome engine, bool naturalCompletion)
    {
        if (raw.State != AdapterObservationState.Unknown || raw.CompletionKind == AdapterCompletion.NotExecuted || raw.ReasonCode == OpaqueTask)
            return raw;
        if (engine is EngineOutcome.Succeeded or EngineOutcome.Failed || naturalCompletion)
            return raw with { State = AdapterObservationState.Failed };
        return raw;
    }

    private static bool ProtectedChild(ImmutableArray<AdapterTask> tasks, AdapterTaskObservation[] observations, string taskId) =>
        tasks.Single(task => task.TaskId == taskId).Dependencies.Any(parent => observations.Any(value => value.TaskId == parent &&
            value.State == AdapterObservationState.Partial && value.ReasonCode is "okww_parent_completed_child_failure_retry_forbidden" or
                "zzzonedragon_parent_succeeded_charge_failed_retry_forbidden"));

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
