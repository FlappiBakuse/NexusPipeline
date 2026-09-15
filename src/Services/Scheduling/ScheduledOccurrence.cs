using NexusPipeline.Services.Execution;

namespace NexusPipeline.Services;

internal sealed class ScheduledOccurrence
{
    public string QueueId { get; init; } = "";

    public string QueueName { get; set; } = "";

    public string OccurrenceKey { get; init; } = "";

    public DateTime OriginalTriggerTime { get; init; }

    public bool IsStartup { get; init; }

    public string Status { get; set; } = "Triggered";

    public int RetryCount { get; set; }

    public string LastReason { get; set; } = "";

    public DateTime NextAttemptAt { get; set; }

    public QueueExecutionPlan? Plan { get; set; }

    public string Key => SchedulerTriggerPlanner.TriggerKey(QueueId, OccurrenceKey);
}
