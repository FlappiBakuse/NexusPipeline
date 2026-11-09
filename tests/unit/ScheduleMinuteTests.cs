using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Scheduling;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ScheduleMinuteTests
{
    private static readonly Guid Instance = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 3, 4, 40, TimeSpan.Zero);

    [Fact(DisplayName = "A Stale Watermark Selects Only The Current Minute And Never Backfills The Missed One")]
    public void SelectsCurrentMinute()
    {
        var scan = ScheduleMinutePolicy.Scan(Instance, Catalog(localMinute: 183), new(1, Minute(Now.AddHours(-3))), Now);
        Assert.Empty(scan.Occurrences);
        Assert.Equal(Minute(Now), scan.HighestUtcMinute);
    }

    [Fact(DisplayName = "A Repeated Tick And Wall Clock Rollback Cannot Reduce The Durable Watermark")]
    public void KeepsDurableWatermark()
    {
        var first = ScheduleMinutePolicy.Scan(Instance, Catalog(), new(0, null), Now);
        Assert.Single(first.Occurrences);
        var repeated = ScheduleMinutePolicy.Scan(Instance, Catalog(), new(1, first.HighestUtcMinute), Now.AddSeconds(10));
        var rolledBack = ScheduleMinutePolicy.Scan(Instance, Catalog(), new(1, first.HighestUtcMinute), Now.AddHours(-1));
        Assert.Empty(repeated.Occurrences); Assert.Empty(rolledBack.Occurrences);
        Assert.Equal(first.HighestUtcMinute, rolledBack.HighestUtcMinute);
    }

    [Fact(DisplayName = "Revision Changes Cannot Invent Another Identity For The Same Local Occurrence")]
    public void KeepsOccurrenceIdentity()
    {
        var catalog = Catalog();
        var first = Assert.Single(ScheduleMinutePolicy.Scan(Instance, catalog, new(0, null), Now).Occurrences);
        var changed = catalog with { Revision = 2, Schedules = [catalog.Schedules[0] with { Revision = 2 }] };
        var second = Assert.Single(ScheduleMinutePolicy.Scan(Instance, changed, new(0, null), Now).Occurrences);
        Assert.Equal(first.OccurrenceId, second.OccurrenceId); Assert.Equal(first.RunId, second.RunId);
        Assert.Equal(first.StartOperationId, second.StartOperationId);
        Assert.NotEqual(second.RunId, second.StartOperationId); Assert.NotEqual(first.ScheduleRevision, second.ScheduleRevision);
    }

    [Theory(DisplayName = "Disabled Or Empty Queue Definitions Do Not Create Occurrences")]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void SkipsDisabledQueues(bool enabled, bool emptyQueue)
    {
        var catalog = Catalog(enabled: enabled);
        if (emptyQueue) catalog = catalog with { Queues = [catalog.Queues[0] with { ScriptIds = [] }] };
        Assert.Empty(ScheduleMinutePolicy.Scan(Instance, catalog, new(0, null), Now).Occurrences);
    }

    [Fact(DisplayName = "Repeated Daylight Saving Local Minutes Have The Same Identity")]
    public void DeduplicatesDstMinutes()
    {
        var catalog = Catalog(localMinute: 90, zone: "America/New_York", days: [0]);
        var firstTime = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var secondTime = firstTime.AddHours(1);
        var first = Assert.Single(ScheduleMinutePolicy.Scan(Instance, catalog, new(0, null), firstTime).Occurrences);
        var second = Assert.Single(ScheduleMinutePolicy.Scan(Instance, catalog, new(1, Minute(firstTime)), secondTime).Occurrences);
        Assert.Equal(first.OccurrenceId, second.OccurrenceId); Assert.Equal(first.RunId, second.RunId);
        Assert.NotEqual(first.TriggerUtcMinute, second.TriggerUtcMinute);
    }

    [Theory(DisplayName = "The Maintenance Window Includes Exactly Five Minutes And Ignores Later Occurrences")]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void BoundsMaintenanceWindow(int delta, bool blocked)
    {
        var now = Now.AddSeconds(-40);
        var result = ScheduleMinutePolicy.Upcoming(Catalog(localMinute: 184 + delta), now, TimeSpan.FromMinutes(5));
        Assert.Equal(blocked, result is not null);
    }

    [Fact]
    public void IsolatesInvalidZones()
    {
        foreach (var enabled in new[] { false, true })
        {
            var catalog = Catalog(); var valid = catalog.Schedules[0];
            var bad = valid with { Id = Guid.NewGuid(), IsEnabled = enabled, TimeZoneId = "nexus/no-such-zone" };
            catalog = catalog with { Schedules = [bad, valid] };
            var diagnostics = new List<ScheduleDiagnostic>();
            Assert.Equal(valid.Id, Assert.Single(ScheduleMinutePolicy.Scan(Instance, catalog, new(0, null), Now, diagnostics.Add).Occurrences).ScheduleId);
            Assert.Equal(enabled ? 1 : 0, diagnostics.Count);
            if (enabled) Assert.Equal(new ScheduleDiagnostic(bad.Id, "schedule_time_zone_unavailable"), diagnostics[0]);
            Assert.Equal(valid.Id, ScheduleMinutePolicy.Upcoming(catalog, Now, TimeSpan.FromMinutes(1))!.ScheduleId);
        }
    }

    [Fact]
    public void DeduplicatesQueueTriggers()
    {
        var catalog = Catalog(); var first = catalog.Schedules[0];
        var overlapping = first with { Id = Guid.NewGuid(), Weekdays = [3, 4] };
        catalog = catalog with { Schedules = [first, overlapping] };
        var one = Assert.Single(ScheduleMinutePolicy.Scan(Instance, catalog, new(0, null), Now).Occurrences);
        var secondQueue = catalog.Queues[0] with { Id = Guid.NewGuid(), Name = "Independent", Order = 1 };
        catalog = catalog with { Queues = catalog.Queues.Add(secondQueue),
            Schedules = catalog.Schedules.Add(first with { Id = Guid.NewGuid(), QueueId = secondQueue.Id }) };
        Assert.Equal(new[] { first.QueueId, secondQueue.Id }.Order(),
            ScheduleMinutePolicy.Scan(Instance, catalog, new(0, null), Now).Occurrences.Select(item => item.QueueId).Order());
        Assert.Empty(ScheduleMinutePolicy.Scan(Instance, catalog, new(1, one.TriggerUtcMinute), Now).Occurrences);
    }

    private static long Minute(DateTimeOffset value) => value.ToUnixTimeSeconds() / 60;
    [Fact]
    public void PreservesDisabledTimers()
    {
        var catalog = Catalog();
        var frozen = Assert.Single(ScheduleMinutePolicy.Scan(Instance, catalog, new(0, null), Now).Occurrences);
        var paused = catalog with { Revision = 2, Queues = [catalog.Queues[0] with { Revision = 2, ScheduleEnabled = false }] };
        Assert.Empty(ScheduleMinutePolicy.Scan(Instance, paused, new(0, null), Now).Occurrences);
        Assert.Null(ScheduleMinutePolicy.Upcoming(paused, Now, TimeSpan.FromMinutes(5)));
        Assert.Equal("execution_schedule_cancelled", Assert.Throws<NexusPipeline.Core.Execution.ExecutionSelectionException>(
            () => ScheduledSourcePolicy.RequireCurrent(frozen, paused)).Message);
        var resumed = paused with { Revision = 3, Queues = [paused.Queues[0] with { Revision = 3, ScheduleEnabled = true }] };
        Assert.Equal(frozen.OccurrenceId, Assert.Single(ScheduleMinutePolicy.Scan(Instance, resumed, new(0, null), Now).Occurrences).OccurrenceId);
        var timerDisabled = resumed with { Schedules = [resumed.Schedules[0] with { IsEnabled = false }] };
        Assert.Empty(ScheduleMinutePolicy.Scan(Instance, timerDisabled, new(0, null), Now).Occurrences);
    }

    [Fact]
    public void FindsNextWeekday()
    {
        var catalog = Catalog(localMinute: 540, zone: "Asia/Shanghai", days: [3]);
        var next = Assert.Single(ScheduleMinutePolicy.NextForUsers(catalog, Now));
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 1, 0, 0, TimeSpan.Zero), next.NextRunAt);
        Assert.Equal(catalog.Users[0].Id, next.UserId);
    }

    [Fact]
    public void ChoosesEarliestQueue()
    {
        var catalog = Catalog(localMinute: 600);
        var second = catalog.Queues[0] with { Id = Guid.Parse("60000000-0000-0000-0000-000000000002"), Name = "Second" };
        catalog = catalog with { Queues = catalog.Queues.Add(second), Schedules = catalog.Schedules.Add(catalog.Schedules[0] with
            { Id = Guid.NewGuid(), QueueId = second.Id, TimeZoneId = "Asia/Shanghai", LocalMinute = 720 }) };
        var next = Assert.Single(ScheduleMinutePolicy.NextForUsers(catalog, Now));
        Assert.Equal(second.Id, next.QueueId);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 4, 0, 0, TimeSpan.Zero), next.NextRunAt);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("schedule")]
    [InlineData("queue")]
    [InlineData("days")]
    [InlineData("user")]
    public void OmitsDisabledBindings(string scope)
    {
        var catalog = Catalog();
        catalog = scope switch {
            "binding" => catalog with { Bindings = [catalog.Bindings[0] with { IsEnabled = false }] },
            "schedule" => catalog with { Schedules = [catalog.Schedules[0] with { IsEnabled = false }] },
            "queue" => catalog with { Queues = [catalog.Queues[0] with { ScheduleEnabled = false }] },
            "days" => catalog with { Bindings = [catalog.Bindings[0] with { RunDays = 0 }] },
            _ => catalog with { Users = [catalog.Users[0] with { BindingOverrides = new() { General = new(true, false, 3, 2) } }] }
        };
        Assert.Empty(ScheduleMinutePolicy.NextForUsers(catalog, Now));
    }

    [Fact]
    public void PreservesRemainingDays()
    {
        var catalog = Catalog(days: [3]);
        catalog = catalog with { Bindings = [catalog.Bindings[0] with { RunDays = 2 }] };
        Assert.Single(ScheduleMinutePolicy.NextForUsers(catalog, Now));
    }

    private static AutomationCatalog Catalog(int localMinute = 184, bool enabled = true, string zone = "UTC", ImmutableArray<int> days = default)
    {
        var root = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var script = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var user = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var binding = Guid.Parse("50000000-0000-0000-0000-000000000001");
        var queue = Guid.Parse("60000000-0000-0000-0000-000000000001");
        return new(1, [new(root, 1, "root", AutomationRootKind.Script, "controlled-root")],
            [new(script, 1, "script", AutomationKind.Generic, root, null, new(root, "fixture.exe"), [], [], [],
                new(1, 30, 120), new(AutomationJudgementLanguage.Keywords, [], [], null))],
            [new(user, 1, "user", 0)], [new(binding, 1, user, script, true, 0)],
            [new(queue, 1, "queue", [script], AutomationCompletionAction.None)],
            [new(Guid.Parse("70000000-0000-0000-0000-000000000001"), 1, queue, enabled, zone, days.IsDefault ? [3] : days, localMinute)]);
    }
}
