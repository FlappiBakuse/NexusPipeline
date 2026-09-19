using Xunit;
using NexusPipeline.Modules.Execution;
namespace NexusPipeline.Tests.Execution;


public sealed class HostMaintenanceLeaseTests
{

    [Fact]
    public void HostMaintenanceLease_AtomicallyBlocksNewExecutionAndEditAdmission()
    {
        var store = new ExecutionStateStore();

        HostMaintenanceLease? lease = store.TryAcquireMaintenanceLease(out string reason);

        Assert.NotNull(lease);
        Assert.Equal("", reason);
        Assert.Equal(ExecutionGroupState.Maintenance, store.GroupState);

        var execution = new RunningExecution
        {
            Kind = "script",
            TargetId = "script-maintenance",
            TargetName = "维护期间脚本",
        };
        Assert.False(store.TryRegister(
            execution,
            new ExecutionAdmissionProfile("script", ExecutionConcurrencyClass.Standard, ExecutionResourceSet.Empty, "none"),
            out ExecutionAdmissionFailure? admissionFailure));
        Assert.Equal(ExecutionAdmissionFailureCode.HostMaintenance, admissionFailure!.Code);

        Assert.False(store.TryBeginEditSession("script-maintenance", "user", @"C:\config.json", out string? editConflict));
        Assert.Contains("维护", editConflict);

        lease!.Dispose();
        Assert.Equal(ExecutionGroupState.Open, store.GroupState);
        Assert.True(store.TryRegister(
            execution,
            new ExecutionAdmissionProfile("script", ExecutionConcurrencyClass.Standard, ExecutionResourceSet.Empty, "none"),
            out _));
        store.Unregister(execution);
    }

    [Fact]
    public void HostMaintenanceLease_blocks_configuration_mutations_inside_coordination_domain()
    {
        var store = new ExecutionStateStore();
        using HostMaintenanceLease lease = store.TryAcquireMaintenanceLease(out string reason)!;
        bool mutated = false;

        Assert.False(store.TryExecuteLeaseMutation(
            "script-maintenance",
            null,
            () => mutated = true,
            out IReadOnlyList<ExecutionLeaseReference> leases,
            out string? failureCode));
        Assert.False(mutated);
        Assert.Empty(leases);
        Assert.Equal("host_maintenance", failureCode);
        Assert.Equal("", reason);
    }
}
