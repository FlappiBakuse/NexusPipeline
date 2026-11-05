namespace NexusPipeline.Core.Scheduling;

public static class WorkNextPolicy
{
    public static DateTimeOffset? Find(WorkTaskPlan task, DateTimeOffset afterUtc, Func<DateOnly, int, bool> wasAccepted)
    {
        ArgumentNullException.ThrowIfNull(wasAccepted);
        WorkPlanPolicy.Validate(new(task.Revision, [task]));
        if (afterUtc.Offset != TimeSpan.Zero || afterUtc <= DateTimeOffset.UnixEpoch) throw new ArgumentException("work_clock_invalid");
        if (!task.IsEnabled) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(task.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(afterUtc, zone).DateTime);
        DateTimeOffset? next = null;
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = today.AddDays(offset);
            if (!task.Weekdays.Contains((int)date.DayOfWeek) || wasAccepted(date, task.LocalMinute)) continue;
            var local = date.ToDateTime(new TimeOnly(task.LocalMinute / 60, task.LocalMinute % 60));
            if (zone.IsInvalidTime(local)) continue;
            var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
            foreach (var utcOffset in offsets)
            {
                var candidate = new DateTimeOffset(local, utcOffset).ToUniversalTime();
                if (candidate > afterUtc && (next is null || candidate < next)) next = candidate;
            }
        }
        return next;
    }
}
