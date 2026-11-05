using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal static class PreviewDomainRecovery
{
    internal static void Reconcile(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, Guid parentJournalId) => lease.PublishExecution(() =>
    {
        lease.VerifyHeld(instance);
        var state = AttemptWriterState.Read(instance, database, contracts, parentJournalId);
        PreviewDomainEvidence.RequireStopped(database, state);
        if (!state.IsDispatchClosed) throw PreviewDomainCodec.Invalid();
        foreach (var pending in PreviewDomainCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).Where(value => !value.Closed))
        {
            PreviewDomainEvidence.RequireColdStopped(pending.Source);
            PreviewDomainSession.Append(instance, database, contracts, state,
                new(PreviewDomainCodec.Format, 1, pending.Source, "closed", state.Latest.Revision, null), null);
            state = AttemptWriterState.Read(instance, database, contracts, parentJournalId);
        }
        return true;
    });
}
