using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Scheduling;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class WorkPlanningTests
{
    [Fact(DisplayName = "A Plan Observation Reads The Registered Source And Cannot Authorize Work After Revocation")]
    public async Task RechecksRegisteredSource()
    {
        var registry = new ScheduledWorkRegistry(new ComponentAuthorization());
        var activation = new ExtensionActivation("fixture"); activation.Publish();
        var source = new PlansSource();
        using var registration = registry.Register(activation, "daily", source);
        var entry = Assert.Single(registry.PublishedPlanningEntries());
        var observed = await entry.ReadPlansAsync();
        Assert.Equal(1, source.Reads); Assert.Same(entry, observed.Entry);
        Assert.Equal(1, observed.Plans.Revision); Assert.Equal(source.TaskId, Assert.Single(observed.Plans.Tasks).TaskId);
        var due = Assert.Single(ScheduleMinutePolicy.ProviderOccurrences(Guid.NewGuid(), observed, Now));
        Assert.Equal(source.TaskId, due.Invocation.SubjectId); Assert.Equal(source.TaskId, due.Task.TaskId);
        activation.Revoke();
        Assert.Throws<InvalidOperationException>(() => entry.Use(() => true));
        Assert.Throws<InvalidOperationException>(() => ScheduleMinutePolicy.ProviderOccurrences(Guid.NewGuid(), observed, Now));
        Assert.Equal(0, source.Executions);
    }

    [Fact(DisplayName = "Replacing A Registration Cannot Dispatch The Replacement Through An Old Entry")]
    public async Task RejectsReplacedRegistrations()
    {
        var registry = new ScheduledWorkRegistry(new ComponentAuthorization());
        var activation = new ExtensionActivation("fixture"); activation.Publish();
        var original = new PlansSource();
        var registered = registry.Register(activation, "daily", original);
        var entry = Assert.Single(registry.PublishedPlanningEntries());
        var observed = await entry.ReadPlansAsync();
        var invocation = Assert.Single(ScheduleMinutePolicy.ProviderOccurrences(Guid.NewGuid(), observed, Now)).Invocation;
        registered.Dispose();
        var replacement = new PlansSource();
        using var current = registry.Register(activation, "daily", replacement);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await entry.DispatchAsync(invocation));
        Assert.Equal(0, original.Executions); Assert.Equal(0, replacement.Executions);
    }

    private static DateTimeOffset Now => new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class ComponentAuthorization : IScheduledInvocationAuthorization
    {
        public void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId) { }
    }

    private sealed class PlansSource : IWorkPlanSource
    {
        internal Guid TaskId { get; } = Guid.NewGuid();
        internal int Reads, Executions;
        internal ScheduledWorkPlans Plans => new(1, [new(TaskId, 1, true, "UTC", [(int)Now.DayOfWeek],
            12 * 60, 120, ImmutableArray<Guid>.Empty)]);
        public ValueTask<ScheduledWorkPlans> ReadPlansAsync(CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Reads++; return ValueTask.FromResult(Plans); }
        public ValueTask<ScheduledBusinessResult> ExecutePlannedAsync(PlannedScheduledInvocation invocation, CancellationToken cancellation) =>
            ExecuteAsync(invocation.Invocation, cancellation);
        public ValueTask<ScheduledBusinessResult> ExecuteAsync(FrozenScheduledInvocation invocation, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Executions++; return ValueTask.FromResult(new ScheduledBusinessResult(BusinessOutcome.Unverified, "component_callback")); }
    }
}
