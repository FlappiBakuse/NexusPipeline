using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Scheduling;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class PlannedInvocationTests
{
    [Fact(DisplayName = "The Planned Entry Routes Both Revisions Without Calling The Unversioned Component Entry")]
    public async Task RoutesPlannedRevisions()
    {
        var source = new Source(); var invocation = Invocation(source.TaskId);
        var registry = new ScheduledWorkRegistry(new BoundPolicy(invocation));
        var activation = new ExtensionActivation("fixture"); activation.Publish();
        using var registration = registry.Register(activation, "daily", source);
        var entry = Assert.Single(registry.PublishedPlanningEntries());
        var result = await entry.DispatchPlannedAsync(new(invocation, 3, 2));
        Assert.Equal(BusinessOutcome.Unverified, result.Business);
        Assert.Equal(1, source.PlannedCalls); Assert.Equal(0, source.ComponentCalls);
        Assert.NotNull(source.Last); Assert.Equal(3, source.Last.PlansRevision); Assert.Equal(2, source.Last.TaskRevision);
    }

    [Theory(DisplayName = "A Context With Different Saved Revisions Cannot Reach The Registered Planned Callback")]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public async Task RejectsChangedRevisions(long plans, long task)
    {
        var source = new Source(); var invocation = Invocation(source.TaskId);
        var registry = new ScheduledWorkRegistry(new BoundPolicy(invocation));
        var activation = new ExtensionActivation("fixture"); activation.Publish();
        using var registration = registry.Register(activation, "daily", source);
        var entry = Assert.Single(registry.PublishedPlanningEntries());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await entry.DispatchPlannedAsync(new(invocation, plans, task)));
        Assert.Equal(0, source.PlannedCalls); Assert.Equal(0, source.ComponentCalls);
    }

    [Fact(DisplayName = "A Registry Without Planned Authorization Cannot Fallback To The Unversioned Callback")]
    public async Task RejectsUnversionedFallback()
    {
        var source = new Source(); var registry = new ScheduledWorkRegistry(new ComponentOnlyPolicy());
        var activation = new ExtensionActivation("fixture"); activation.Publish();
        using var registration = registry.Register(activation, "daily", source);
        var entry = Assert.Single(registry.PublishedPlanningEntries());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await entry.DispatchPlannedAsync(new(Invocation(source.TaskId), 3, 2)));
        Assert.Equal(0, source.PlannedCalls); Assert.Equal(0, source.ComponentCalls);
    }

    private static FrozenScheduledInvocation Invocation(Guid task) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), task, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), []);
    private sealed class BoundPolicy(FrozenScheduledInvocation expected) : IPlannedInvocationAuthorization
    {
        public void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId) =>
            throw new UnauthorizedAccessException("scheduled_planned_context_required");
        public void AssertAllowed(PlannedScheduledInvocation invocation, string extensionId, string providerId)
        {
            if (!invocation.Invocation.Matches(expected) || invocation.PlansRevision != 3 || invocation.TaskRevision != 2 ||
                extensionId != "fixture" || providerId != "daily") throw new UnauthorizedAccessException("scheduled_planned_context_unbound");
        }
    }
    private sealed class ComponentOnlyPolicy : IScheduledInvocationAuthorization
    {
        public void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId) { }
    }
    private sealed class Source : IWorkPlanSource
    {
        internal Guid TaskId { get; } = Guid.NewGuid();
        internal int PlannedCalls, ComponentCalls;
        internal PlannedScheduledInvocation? Last;
        public ValueTask<ScheduledWorkPlans> ReadPlansAsync(CancellationToken cancellation) =>
            ValueTask.FromResult(new ScheduledWorkPlans(3, [new(TaskId, 2, true, "UTC", [0], 0, 120, ImmutableArray<Guid>.Empty)]));
        public ValueTask<ScheduledBusinessResult> ExecuteAsync(FrozenScheduledInvocation invocation, CancellationToken cancellation)
        { ComponentCalls++; return ValueTask.FromResult(new ScheduledBusinessResult(BusinessOutcome.Unverified, "component_only")); }
        public ValueTask<ScheduledBusinessResult> ExecutePlannedAsync(PlannedScheduledInvocation invocation, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); PlannedCalls++; Last = invocation;
            return ValueTask.FromResult(new ScheduledBusinessResult(BusinessOutcome.Unverified, "planned_component"));
        }
    }
}
