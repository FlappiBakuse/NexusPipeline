using NexusPipeline.Modules.Queues;

namespace NexusPipeline.Modules.Scheduling;

/// <summary>
/// 计算调度时间和 occurrence 标识。这里不访问持久化、运行时或任务执行服务。
/// </summary>
internal static class SchedulerTriggerPlanner
{
    internal static DateTime? NextTriggerFor(DispatchQueue queue, DateTime now)
    {
        if (queue.AutoRunMode != "scheduled" || queue.Tasks.Count == 0)
        {
            return null;
        }
        var candidates = new List<DateTime>();
        foreach (QueueTimeSet timeSet in queue.TimeSets.Where(timeSet => timeSet.Enabled))
        {
            if (!TimeOnly.TryParseExact(
                    timeSet.Time,
                    "HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out TimeOnly timeOnly))
            {
                continue;
            }
            for (int offset = 0; offset < 7; offset++)
            {
                DateTime candidate = now.Date.AddDays(offset).Add(timeOnly.ToTimeSpan());
                if (candidate > now && timeSet.Days.Contains((int)candidate.DayOfWeek))
                {
                    candidates.Add(candidate);
                    break;
                }
            }
        }
        return candidates.Count == 0 ? null : candidates.Min();
    }

    internal static IEnumerable<(string OccurrenceKey, DateTime TriggerTime)> EnumerateOccurrences(
        DispatchQueue queue,
        DateTime from,
        DateTime to)
    {
        for (DateTime date = from.Date; date <= to.Date; date = date.AddDays(1))
        {
            foreach (QueueTimeSet timeSet in queue.TimeSets.Where(item => item.Enabled))
            {
                if (!TimeOnly.TryParseExact(
                        timeSet.Time,
                        "HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out TimeOnly timeOnly))
                {
                    continue;
                }
                DateTime candidate = date.Add(timeOnly.ToTimeSpan());
                if (candidate > from && candidate <= to && timeSet.Days.Contains((int)candidate.DayOfWeek))
                {
                    yield return ($"{candidate:yyyy-MM-dd HH:mm}", candidate);
                }
            }
        }
    }

    internal static DateTime ScheduledScanStart(DateTime now)
    {
        DateTime minuteStart = new(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        return minuteStart.AddTicks(-1);
    }

    internal static bool MatchesOccurrence(DispatchQueue queue, DateTime triggerTime)
    {
        return queue.AutoRunMode == "scheduled"
            && queue.Tasks.Count > 0
            && queue.TimeSets.Any(timeSet =>
                timeSet.Enabled
                && timeSet.Days.Contains((int)triggerTime.DayOfWeek)
                && string.Equals(timeSet.Time, triggerTime.ToString("HH:mm"), StringComparison.Ordinal));
    }

    internal static string TriggerKey(string queueId, string occurrenceKey)
    {
        return $"{queueId}\n{occurrenceKey}";
    }

    internal static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining.TotalMinutes >= 1)
        {
            return $"{Math.Ceiling(remaining.TotalMinutes):0} 分钟";
        }
        return $"{Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):0} 秒";
    }
}
