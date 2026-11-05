using NexusPipeline.Core.Scheduling;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class WorkNextTests
{
    [Theory(DisplayName = "Host Next Work Time Uses Saved Weekdays And Excludes Already Accepted Local Occurrences")]
    [InlineData(false)]
    [InlineData(true)]
    public void ExcludesAcceptedDays(bool accepted)
    {
        var task = new WorkTaskPlan(Guid.NewGuid(), 1, true, "UTC", [1], 600, 60, []);
        var now = new DateTimeOffset(2026, 10, 26, 9, 30, 0, TimeSpan.Zero);
        var next = WorkNextPolicy.Find(task, now, (_, _) => accepted);
        if (accepted) Assert.Null(next);
        else Assert.Equal(now.AddMinutes(30), next);
        Assert.Null(WorkNextPolicy.Find(task with { IsEnabled = false }, now, (_, _) => false));
    }

    [Fact(DisplayName = "A Repeated Local Minute Has Two UTC Candidates But Only One Accepted Local Occurrence")]
    public void HandlesRepeatedTime()
    {
        var task = new WorkTaskPlan(Guid.NewGuid(), 1, true, "America/New_York", [0, 1], 90, 60, []);
        var now = new DateTimeOffset(2026, 11, 1, 5, 20, 0, TimeSpan.Zero);
        Assert.Equal(now.AddMinutes(10), WorkNextPolicy.Find(task, now, (_, _) => false));
        var afterFirst = now.AddMinutes(20);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), WorkNextPolicy.Find(task, afterFirst, (_, _) => false));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero),
            WorkNextPolicy.Find(task, afterFirst, (date, minute) => date == new DateOnly(2026, 11, 1) && minute == 90));
    }

    [Fact(DisplayName = "A Skipped Local Minute Does Not Become An Invented UTC Schedule")]
    public void SkipsMissingTime()
    {
        var task = new WorkTaskPlan(Guid.NewGuid(), 1, true, "America/New_York", [0, 1], 150, 60, []);
        var now = new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero), WorkNextPolicy.Find(task, now, (_, _) => false));
    }
}
