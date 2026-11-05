using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class PreparedUpdateHandoff : IDisposable
{
    private readonly TreeReadLease _before;
    private readonly PreparedPayloadPlan _frozen;
    internal HostCompletionDrain Drain { get; }
    internal ResourceOperationLease Root { get; }
    internal HostUpdateSource Source { get; }
    internal Guid HandoffId { get; }
    internal FrozenPayloadApply Plan => _frozen.Plan;
    internal BlobReference PlanReference => _frozen.Reference;

    internal PreparedUpdateHandoff(HostCompletionDrain drain, ResourceOperationLease root, TreeReadLease before,
        PreparedPayloadPlan frozen, HostUpdateSource source, Guid handoffId)
    { Drain = drain; Root = root; _before = before; _frozen = frozen; Source = source; HandoffId = handoffId; }

    public void Dispose()
    {
        try { _frozen.Dispose(); } finally { try { _before.Dispose(); } finally { Root.Dispose(); } }
    }
}
