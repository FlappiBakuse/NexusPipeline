namespace NexusPipeline.Core.Scheduling;

public sealed record ScheduledProviderOccurrence(FrozenScheduledInvocation Invocation, WorkTaskPlan Task,
    long PlansRevision, long TriggerUtcMinute, DateOnly LocalDate);
