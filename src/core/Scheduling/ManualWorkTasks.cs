using System.Collections.Immutable;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Core.Scheduling;

public sealed record ManualWorkTask(Guid TaskId, long Revision, bool IsEnabled, string TimeZoneId, int TimeoutSeconds,
    ImmutableArray<Guid> NotificationTaskIds, ImmutableArray<Guid> ScheduledTaskIds);
public sealed record ManualInvocation(FrozenScheduledInvocation Invocation, long TaskRevision);

public interface IManualWorkSource : IScheduledWorkSource
{
    string? IdentityVersion => null;
    SecretValue ProjectIdentity(string field, SecretValue material) => throw new NotSupportedException("work_identity_unavailable");
    bool CanRotateIdentity(string field, SecretValue before, SecretValue after) => false;
    ValueTask<ManualWorkTask?> ReadTaskAsync(Guid taskId, CancellationToken cancellation);
    ValueTask<ScheduledBusinessResult> ExecuteManualAsync(ManualInvocation invocation, CancellationToken cancellation);
}

public interface IManualInvocationAuthorization : IScheduledInvocationAuthorization
{
    void AssertAllowed(ManualInvocation invocation, string extensionId, string providerId);
}

public static class ManualWorkPolicy
{
    public static void RequirePlan(ManualWorkTask owner, WorkTaskPlan plan)
    {
        Validate(owner);
        if (!owner.IsEnabled || owner.TaskId != plan.OwnerTaskId || !owner.ScheduledTaskIds.Contains(plan.TaskId) ||
            owner.Revision != plan.Revision || owner.TimeoutSeconds != plan.TimeoutSeconds || owner.TimeZoneId != plan.TimeZoneId ||
            !owner.NotificationTaskIds.SequenceEqual(plan.NotificationTaskIds)) throw new InvalidOperationException("scheduled_task_owner_unbound");
    }

    public static void Validate(ManualWorkTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.TaskId == Guid.Empty || task.Revision is < 1 or > 9007199254740991 ||
            task.TimeZoneId is not { Length: >= 1 and <= 128 } || task.TimeZoneId.Contains('\0') || task.TimeoutSeconds is < 1 or > 604800 ||
            task.ScheduledTaskIds.IsDefault || task.ScheduledTaskIds.Length > 4096 || task.ScheduledTaskIds.Contains(Guid.Empty) ||
            task.ScheduledTaskIds.Distinct().Count() != task.ScheduledTaskIds.Length) throw new ArgumentException("manual_work_task_invalid");
        _ = TimeZoneInfo.FindSystemTimeZoneById(task.TimeZoneId);
        NotificationRoutingPolicy.RequireSelection(task.NotificationTaskIds);
    }
}

public sealed class ObservedManualTask
{
    public PublishedWorkEntry Entry { get; }
    public ManualWorkTask Task { get; }
    internal ObservedManualTask(PublishedWorkEntry entry, ManualWorkTask task) { Entry = entry; Task = task; }
}
