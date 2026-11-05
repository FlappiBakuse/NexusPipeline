using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record CompletionDrainDecision(HostCompletionDrain? Drain, bool Waiting);

internal sealed class CompletionSchedulingBarrier(StateDatabase database, InstanceLease lease,
    ResourceLeaseManager resources, HostLifecycle lifecycle, TimeProvider? clock = null)
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal CompletionDrainDecision Begin(Guid operationId)
    {
        lease.VerifyHeld(lease.Instance);
        using var root = resources.TryAcquire(operationId,
            [new("scheduling:state", ResourceClaimMode.Mutation), new("automation:definitions", ResourceClaimMode.Reference),
                new("settings:host", ResourceClaimMode.Reference)]).Lease;
        if (root is null) return new(null, lifecycle.Phase == HostLifecyclePhase.Running);
        var startup = new StartupQueueStore(lease.Instance, database, lease, resources, clock ?? TimeProvider.System);
        _ = startup.CommitUnderRoot(root);
        return new(lifecycle.BeginCompletionDrain(operationId), false);
    }
}
