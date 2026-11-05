using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Scheduling;

public static class ScheduledSourcePolicy
{
    public static AutomationQueue RequireCurrent(ScheduledOccurrenceProposal frozen, AutomationCatalog catalog)
    {
        AutomationCatalogPolicy.Validate(catalog);
        var schedule = catalog.Schedules.SingleOrDefault(item => item.Id == frozen.ScheduleId);
        var queue = catalog.Queues.SingleOrDefault(item => item.Id == frozen.QueueId);
        if (schedule is null || !schedule.IsEnabled || schedule.QueueId != frozen.QueueId || queue is null || !queue.ScheduleEnabled || queue.ScriptIds.IsEmpty)
            throw Cancelled();
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId); }
        catch (TimeZoneNotFoundException) { throw Cancelled(); }
        catch (InvalidTimeZoneException) { throw Cancelled(); }
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(checked(frozen.TriggerUtcMinute * 60)), zone);
        if (schedule.LocalMinute != local.Hour * 60 + local.Minute || !schedule.Weekdays.Contains((int)local.DayOfWeek)) throw Cancelled();
        return queue;
    }

    private static ExecutionSelectionException Cancelled() => new("execution_schedule_cancelled");
}
