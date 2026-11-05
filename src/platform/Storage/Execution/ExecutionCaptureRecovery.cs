using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal static class ExecutionCaptureRecovery
{
    internal static void Reconcile(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, Guid parentJournalId)
    {
        lease.VerifyHeld(instance);
        var state = AttemptWriterState.Read(instance, database, contracts, parentJournalId);
        foreach (var pending in ExecutionCaptureCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .Where(value => value.Result is null))
            new ExecutionCaptureStore(instance, database, contracts, lease, pending.Binding).RecoverUnavailable();
    }
}
