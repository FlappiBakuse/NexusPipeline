using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Scheduling;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ManualWorkTests
{
    [Theory(DisplayName = "A Public Schedule Cannot Claim An Unrelated Private Task Or A Different Frozen Revision And Runtime Policy")]
    [InlineData("owner")]
    [InlineData("schedule")]
    [InlineData("revision")]
    [InlineData("timeout")]
    [InlineData("zone")]
    [InlineData("notification")]
    [InlineData("available")]
    public void BindsPlanOwner(string fault)
    {
        var schedule = Guid.NewGuid(); var owner = new ManualWorkTask(Guid.NewGuid(), 7, true, "UTC", 60, [], [schedule]);
        var plan = new WorkTaskPlan(schedule, 7, true, "UTC", [1], 600, 60, [], owner.TaskId);
        ManualWorkPolicy.RequirePlan(owner, plan);
        var changed = fault switch
        {
            "owner" => owner with { TaskId = Guid.NewGuid() },
            "schedule" => owner with { ScheduledTaskIds = [Guid.NewGuid()] },
            "revision" => owner with { Revision = 8 },
            "timeout" => owner with { TimeoutSeconds = 61 },
            "zone" => owner with { TimeZoneId = "Asia/Tokyo" },
            "notification" => owner with { NotificationTaskIds = [Guid.NewGuid()] },
            _ => owner with { IsEnabled = false }
        };
        Assert.Throws<InvalidOperationException>(() => ManualWorkPolicy.RequirePlan(changed, plan));
    }

    [Fact(DisplayName = "A Manual Only Provider Exposes Its Actual Task Without An Enabled Schedule Or A Planning Entry")]
    public async Task ReadsUnscheduledTask()
    {
        var source = new Source(); var policy = new Policy(Invocation(source.TaskId));
        var registry = new ScheduledWorkRegistry(policy);
        var activation = new ExtensionActivation("fixture"); activation.Publish();
        using var registration = registry.Register(activation, "tasks", source);
        var entry = registry.RequireManualEntry("fixture", "tasks");
        var observed = await entry.ReadManualAsync(source.TaskId);
        Assert.NotNull(observed); Assert.Equal(7, observed.Task.Revision); Assert.Empty(observed.Task.ScheduledTaskIds);
        Assert.Empty(registry.PublishedPlanningEntries());
        Assert.Null(await entry.ReadManualAsync(Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => registry.RequireManualEntry("another", "tasks"));
    }

    [Fact(DisplayName = "Only The Accepted Manual Invocation And Frozen Task Revision Can Reach Its Actual Manual Callback")]
    public async Task DispatchesBoundContext()
    {
        var source = new Source(); var invocation = Invocation(source.TaskId); var policy = new Policy(invocation);
        var registry = new ScheduledWorkRegistry(policy);
        var activation = new ExtensionActivation("fixture"); activation.Publish();
        using var registration = registry.Register(activation, "tasks", source);
        var entry = registry.RequireManualEntry("fixture", "tasks");
        var result = await entry.DispatchManualAsync(new(invocation, 7));
        Assert.Equal(BusinessOutcome.Unverified, result.Business); Assert.Equal(1, source.Calls);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => entry.DispatchManualAsync(new(invocation, 6)).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => entry.DispatchManualAsync(new(Invocation(source.TaskId), 7)).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => entry.DispatchAsync(invocation).AsTask());
        Assert.Equal(1, source.Calls);
    }

    [Fact(DisplayName = "Revoking A Manual Registration Cancels Its In Flight Callback And Rejects The Captured Entry")]
    public async Task CancelsRevokedProvider()
    {
        var source = new Source { Hold = true }; var invocation = Invocation(source.TaskId);
        var registry = new ScheduledWorkRegistry(new Policy(invocation));
        var activation = new ExtensionActivation("fixture"); activation.Publish();
        using var registration = registry.Register(activation, "tasks", source);
        var entry = registry.RequireManualEntry("fixture", "tasks");
        var running = entry.DispatchManualAsync(new(invocation, 7)).AsTask();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        registration.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => entry.ReadManualAsync(source.TaskId).AsTask());
    }

    [Fact(DisplayName = "A Manual Source Cannot Substitute Another Task Identity Into A Requested Snapshot")]
    public async Task RejectsForeignTask()
    {
        var source = new Source { Substitute = true };
        var registry = new ScheduledWorkRegistry(new Policy(Invocation(source.TaskId)));
        var activation = new ExtensionActivation("fixture"); activation.Publish();
        using var registration = registry.Register(activation, "tasks", source);
        var entry = registry.RequireManualEntry("fixture", "tasks");
        await Assert.ThrowsAsync<InvalidOperationException>(() => entry.ReadManualAsync(source.TaskId).AsTask());
    }

    [Fact(DisplayName = "A Published Manual Task Cannot Reenter Until Its Canceled Callback Returns And Its Quiescence Hold Releases")]
    public async Task QuiescesManualTask()
    {
        var source = new Source { Hold = true, IgnoreCancellation = true }; var invocation = Invocation(source.TaskId);
        var registry = new ScheduledWorkRegistry(new Policy(invocation));
        var activation = new ExtensionActivation("fixture"); activation.Publish();
        using var registration = registry.Register(activation, "tasks", source);
        var entry = registry.RequireManualEntry("fixture", "tasks");
        var running = entry.DispatchManualAsync(new(invocation, 7)).AsTask();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var draining = entry.QuiesceAsync([source.TaskId]).AsTask();
        Assert.True(source.CapturedCancellation.IsCancellationRequested); Assert.False(draining.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => entry.DispatchManualAsync(new(invocation, 7)).AsTask());
        source.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var hold = await draining.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => entry.DispatchManualAsync(new(invocation, 7)).AsTask());
        Assert.Equal(1, source.Calls);
        hold.Dispose(); source.Hold = false;
        _ = await entry.DispatchManualAsync(new(invocation, 7));
        Assert.Equal(2, source.Calls);
    }

    private static FrozenScheduledInvocation Invocation(Guid task) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        task, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), []);
    private sealed class Policy(FrozenScheduledInvocation actual) : IManualInvocationAuthorization
    {
        public void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId) => throw new UnauthorizedAccessException();
        public void AssertAllowed(ManualInvocation invocation, string extensionId, string providerId)
        {
            if (!invocation.Invocation.Matches(actual) || invocation.TaskRevision != 7 || extensionId != "fixture" || providerId != "tasks")
                throw new UnauthorizedAccessException();
        }
    }
    private sealed class Source : IManualWorkSource
    {
        internal Guid TaskId { get; } = Guid.NewGuid();
        internal bool Hold, Substitute, IgnoreCancellation;
        internal int Calls;
        internal CancellationToken CapturedCancellation;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ManualWorkTask?> ReadTaskAsync(Guid taskId, CancellationToken cancellation) =>
            ValueTask.FromResult<ManualWorkTask?>(taskId == TaskId ? new(Substitute ? Guid.NewGuid() : TaskId, 7, true, "UTC", 120, [], []) : null);
        public ValueTask<ScheduledBusinessResult> ExecuteAsync(FrozenScheduledInvocation invocation, CancellationToken cancellation) => throw new Xunit.Sdk.XunitException("unplanned_callback_reached");
        public async ValueTask<ScheduledBusinessResult> ExecuteManualAsync(ManualInvocation invocation, CancellationToken cancellation)
        {
            Calls++; CapturedCancellation = cancellation; Started.TrySetResult();
            if (Hold)
            {
                if (IgnoreCancellation) await Release.Task;
                else await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            return new(BusinessOutcome.Unverified, "controlled_manual_result");
        }
    }
}
