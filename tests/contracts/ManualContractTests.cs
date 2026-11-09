using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using Xunit;
using WireTask = NexusPipeline.Extensibility.Protocol.ManualWorkTask;
using WirePlans = NexusPipeline.Extensibility.Protocol.ScheduledWorkPlans;

namespace NexusPipeline.ContractTests;

public sealed class ManualContractTests
{
    [Theory(DisplayName = "SDK Manual Work Preserves Its Exact Frozen Host Context With Or Without Public Schedules")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindsManualContext(bool planning)
    {
        var fixture = new ScheduledContractFixture();
        var handler = planning ? new Combined(fixture.Invocation.SubjectId) : new Handler(fixture.Invocation.SubjectId);
        fixture.HandlerOverride = handler;
        await using var scope = await fixture.ActivateAsync();
        Assert.Equal(planning ? 1 : 0, fixture.Registry.PublishedPlanningEntries().Count);
        var entry = fixture.Registry.RequireManualEntry("fixture", "daily");
        var observed = await entry.ReadManualAsync(fixture.Invocation.SubjectId);
        Assert.NotNull(observed); Assert.Equal(7, observed.Task.Revision);
        Assert.Equal(handler.Saved.ScheduledTaskIds.Select(Guid.Parse), observed.Task.ScheduledTaskIds);
        Assert.Null(await entry.ReadManualAsync(Guid.NewGuid()));
        var result = await entry.DispatchManualAsync(new(fixture.Invocation, 7));
        Assert.Equal(BusinessOutcome.NotApplicable, result.Business);
        Assert.Equal(1, handler.Calls); Assert.Equal(7, handler.Last!.TaskRevision);
        Assert.Equal(fixture.Invocation.RunId.ToString("D"), handler.Last.Invocation.RunId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => entry.DispatchManualAsync(new(fixture.Invocation, 8)).AsTask());
        Assert.Equal(1, handler.Calls);
        if (planning)
        {
            var plans = await Assert.Single(fixture.Registry.PublishedPlanningEntries()).ReadPlansAsync();
            var plan = Assert.Single(plans.Plans.Tasks);
            Assert.Equal(fixture.Invocation.SubjectId, plan.OwnerTaskId); ManualWorkPolicy.RequirePlan(observed.Task, plan);
        }
    }

    [Theory(DisplayName = "SDK Manual Task Metadata Is Validated Before Host Acceptance Or Any Business Effect")]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("identity")]
    [InlineData("timezone")]
    [InlineData("timeout")]
    [InlineData("schedule-duplicate")]
    public async Task RejectsInvalidMetadata(string fault)
    {
        var fixture = new ScheduledContractFixture();
        var handler = new Handler(fixture.Invocation.SubjectId);
        fixture.HandlerOverride = handler;
        var duplicate = Guid.NewGuid().ToString("D");
        handler.Saved = fault switch
        {
            "version" => handler.Saved with { Version = 9 },
            "revision" => handler.Saved with { Revision = 0 },
            "identity" => handler.Saved with { TaskId = Guid.NewGuid().ToString("D") },
            "timezone" => handler.Saved with { TimeZoneId = "Fixture/Missing" },
            "timeout" => handler.Saved with { TimeoutSeconds = 0 },
            _ => handler.Saved with { ScheduledTaskIds = [duplicate, duplicate] }
        };
        await using var scope = await fixture.ActivateAsync();
        var entry = fixture.Registry.RequireManualEntry("fixture", "daily");
        var error = await Record.ExceptionAsync(() => entry.ReadManualAsync(fixture.Invocation.SubjectId).AsTask());
        Assert.NotNull(error);
        Assert.True(error is JsonException or InvalidOperationException or TimeZoneNotFoundException, error.ToString());
        Assert.Equal(0, handler.Calls);
    }

    private class Handler(Guid taskId) : IManualWorkHandler
    {
        internal WireTask Saved { get; set; } = new()
        {
            Format = "nexus.manual.work-task", Version = 1, TaskId = taskId.ToString("D"), Revision = 7,
            IsEnabled = true, TimeZoneId = "UTC", TimeoutSeconds = 60, NotificationTaskIds = [], ScheduledTaskIds = []
        };
        internal int Calls { get; private set; }
        internal ManualWorkInvocation? Last { get; private set; }
        public ValueTask<WireTask?> ReadTaskAsync(Guid requested, CancellationToken cancellation) =>
            ValueTask.FromResult<WireTask?>(requested == taskId ? Saved : null);
        public ValueTask<ScheduledWorkResult> ExecuteAsync(ScheduledInvocation invocation, CancellationToken cancellation) =>
            throw new UnauthorizedAccessException("fixture_manual_context_required");
        public ValueTask<ScheduledWorkResult> ExecuteManualAsync(ManualWorkInvocation invocation, CancellationToken cancellation)
        {
            Calls++; Last = invocation;
            return ValueTask.FromResult(ScheduledContractFixture.Result(invocation.Invocation));
        }
    }

    private sealed class Combined : Handler, IScheduledWorkPlanningHandler
    {
        private readonly Guid _owner;
        private readonly Guid _schedule = Guid.NewGuid();
        internal Combined(Guid taskId) : base(taskId) { _owner = taskId; Saved = Saved with { ScheduledTaskIds = [_schedule.ToString("D")] }; }
        public ValueTask<WirePlans> ReadPlansAsync(CancellationToken cancellation) => ValueTask.FromResult(new WirePlans
        {
            Format = "nexus.schedule.work-plans", Version = 1, Revision = 7,
            Tasks = [new() { TaskId = _schedule.ToString("D"), OwnerTaskId = _owner.ToString("D"), Revision = 7, IsEnabled = true,
                TimeZoneId = "UTC", Weekdays = [1], LocalMinute = 600, TimeoutSeconds = 60, NotificationTaskIds = [] }]
        });
        public ValueTask<ScheduledWorkResult> ExecutePlannedAsync(ScheduledPlannedInvocation invocation, CancellationToken cancellation) =>
            throw new UnauthorizedAccessException("fixture_planned_unavailable");
    }
}
