using System.Collections.Immutable;
using NexusPipeline.Core.Notifications;

namespace NexusPipeline.Core.Scheduling;

public sealed record WorkTaskPlan(Guid TaskId, long Revision, bool IsEnabled, string TimeZoneId,
    ImmutableArray<int> Weekdays, int LocalMinute, int TimeoutSeconds, ImmutableArray<Guid> NotificationTaskIds, Guid? OwnerTaskId = null);
public sealed record ScheduledWorkPlans(long Revision, ImmutableArray<WorkTaskPlan> Tasks);

public interface IWorkPlanSource : IScheduledWorkSource
{
    ValueTask<ScheduledWorkPlans> ReadPlansAsync(CancellationToken cancellation);
    ValueTask<ScheduledBusinessResult> ExecutePlannedAsync(PlannedScheduledInvocation invocation, CancellationToken cancellation);
}

public static class WorkPlanPolicy
{
    public static void Validate(ScheduledWorkPlans plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (plans.Revision is < 0 or > 9007199254740991 || plans.Tasks.IsDefault || plans.Tasks.Length > 4096 ||
            plans.Tasks.Any(task => task is null) || plans.Tasks.Select(task => task.TaskId).Distinct().Count() != plans.Tasks.Length)
            throw new ArgumentException("scheduled_work_plans_invalid");
        foreach (var task in plans.Tasks)
        {
            if (task.TaskId == Guid.Empty || task.OwnerTaskId == Guid.Empty || task.Revision is < 1 or > 9007199254740991 || plans.Revision < task.Revision ||
                task.TimeZoneId is null || task.TimeZoneId.Length is < 1 or > 128 || task.TimeZoneId.Contains('\0') ||
                task.Weekdays.IsDefaultOrEmpty || task.Weekdays.Length > 7 || task.Weekdays.Any(day => day is < 0 or > 6) ||
                task.Weekdays.Distinct().Count() != task.Weekdays.Length || task.LocalMinute is < 0 or > 1439 ||
                task.TimeoutSeconds is < 1 or > 604800) throw new ArgumentException("scheduled_work_plan_invalid");
            _ = TimeZoneInfo.FindSystemTimeZoneById(task.TimeZoneId);
            NotificationRoutingPolicy.RequireSelection(task.NotificationTaskIds);
        }
    }
}
