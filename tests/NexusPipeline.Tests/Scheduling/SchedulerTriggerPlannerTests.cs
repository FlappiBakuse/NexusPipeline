using Xunit;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scheduling;

namespace NexusPipeline.Tests.Scheduling;

public sealed class SchedulerTriggerPlannerTests
{
    [Fact]
    public void NextTriggerFor_UsesNextEnabledOccurrenceWithinSevenDays()
    {
        DateTime now = new(2026, 1, 5, 10, 0, 0);
        var queue = new DispatchQueue
        {
            AutoRunMode = "scheduled",
            Tasks = [new QueueTask { ScriptInstanceId = "script" }],
            TimeSets =
            [
                new QueueTimeSet { Enabled = true, Days = [(int)now.DayOfWeek], Time = "10:05" },
                new QueueTimeSet { Enabled = false, Days = [(int)now.DayOfWeek], Time = "10:01" },
            ],
        };

        Assert.Equal(now.AddMinutes(5), SchedulerTriggerPlanner.NextTriggerFor(queue, now));
    }

    [Fact]
    public void EnumerateOccurrences_UsesExclusiveStartAndInclusiveEnd()
    {
        DateTime from = new(2026, 1, 5, 10, 0, 0);
        DateTime to = new(2026, 1, 5, 10, 1, 0);
        var queue = new DispatchQueue
        {
            AutoRunMode = "scheduled",
            Tasks = [new QueueTask { ScriptInstanceId = "script" }],
            TimeSets =
            [new QueueTimeSet { Enabled = true, Days = [(int)from.DayOfWeek], Time = "10:01" }],
        };

        (string OccurrenceKey, DateTime TriggerTime)[] occurrences =
            SchedulerTriggerPlanner.EnumerateOccurrences(queue, from, to).ToArray();

        Assert.Single(occurrences);
        Assert.Equal("2026-01-05 10:01", occurrences[0].OccurrenceKey);
        Assert.Equal(to, occurrences[0].TriggerTime);
    }
}
