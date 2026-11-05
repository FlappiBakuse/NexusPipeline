using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Extensibility.Scheduling;

/// <summary>Reads saved public schedule and notification choices; business payload and secrets remain extension-owned.</summary>
public interface IScheduledWorkPlanningHandler : IScheduledWorkHandler
{
    /// <summary>Returns one bounded current snapshot; this method must not execute a task or create a scheduling loop.</summary>
    ValueTask<ScheduledWorkPlans> ReadPlansAsync(CancellationToken cancellationToken);

    /// <summary>Atomically checks both saved revisions before any task effect; every private business configuration change must advance task revision.</summary>
    ValueTask<ScheduledWorkResult> ExecutePlannedAsync(ScheduledPlannedInvocation invocation, CancellationToken cancellationToken);
}
