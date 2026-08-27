using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class HostRestartCoordinatorTests
{
    [Fact]
    public void Accepted_restart_acquires_maintenance_before_background_launch()
    {
        var store = new ExecutionStateStore();
        Action? scheduled = null;
        var coordinator = CreateCoordinator(
            store,
            launchChild: () => true,
            requestExit: () => true,
            schedule: work => scheduled = work);

        RestartRequestResult result = coordinator.Request("test", 58731);

        Assert.True(result.Accepted);
        Assert.Equal("ok", result.Code);
        Assert.NotNull(scheduled);
        Assert.Equal(ExecutionGroupState.Maintenance, store.GroupState);

        var execution = new RunningExecution
        {
            Kind = "script",
            TargetId = "restart-blocked",
            TargetName = "重启期间脚本",
        };
        Assert.False(store.TryRegister(
            execution,
            new ExecutionAdmissionProfile("script", ExecutionConcurrencyClass.Standard, ExecutionResourceSet.Empty, "none"),
            out ExecutionAdmissionFailure? failure));
        Assert.Equal(ExecutionAdmissionFailureCode.HostMaintenance, failure!.Code);
    }

    [Fact]
    public void Child_launch_failure_releases_maintenance_lease()
    {
        var store = new ExecutionStateStore();
        Action? scheduled = null;
        var coordinator = CreateCoordinator(
            store,
            launchChild: () => false,
            requestExit: () => true,
            schedule: work => scheduled = work);

        Assert.True(coordinator.Request("test", 58731).Accepted);
        scheduled!.Invoke();

        Assert.Equal(ExecutionGroupState.Open, store.GroupState);
        var execution = new RunningExecution
        {
            Kind = "script",
            TargetId = "restart-released",
            TargetName = "重启失败后脚本",
        };
        Assert.True(store.TryRegister(
            execution,
            new ExecutionAdmissionProfile("script", ExecutionConcurrencyClass.Standard, ExecutionResourceSet.Empty, "none"),
            out _));
        store.Unregister(execution);
    }

    [Fact]
    public void Launched_child_with_delayed_exit_keeps_maintenance_lease()
    {
        var store = new ExecutionStateStore();
        Action? scheduled = null;
        var coordinator = CreateCoordinator(
            store,
            launchChild: () => true,
            requestExit: () => false,
            schedule: work => scheduled = work);

        Assert.True(coordinator.Request("test", 58731).Accepted);
        scheduled!.Invoke();

        Assert.Equal(ExecutionGroupState.Maintenance, store.GroupState);
    }

    [Fact]
    public void Scheduling_failure_releases_maintenance_lease_and_reports_busy()
    {
        var store = new ExecutionStateStore();
        var coordinator = CreateCoordinator(
            store,
            launchChild: () => true,
            requestExit: () => true,
            schedule: _ => throw new InvalidOperationException("scheduler unavailable"));

        RestartRequestResult result = coordinator.Request("test", 58731);

        Assert.False(result.Accepted);
        Assert.Equal("service_busy", result.Code);
        Assert.Equal(ExecutionGroupState.Open, store.GroupState);
    }

    private static HostRestartCoordinator CreateCoordinator(
        ExecutionStateStore store,
        Func<bool> launchChild,
        Func<bool> requestExit,
        Action<Action> schedule)
    {
        return new HostRestartCoordinator(
            acquireMaintenance: () =>
            {
                HostMaintenanceLease? lease = store.TryAcquireMaintenanceLease(out string reason);
                return (lease, lease is null ? reason : null);
            },
            launchChild,
            requestExit,
            schedule,
            _ => { },
            TimeSpan.Zero);
    }
}
