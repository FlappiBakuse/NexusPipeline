using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Core.Configuration;

public sealed class SnapshotCompletionPermit
{
    private readonly RecoveryIndex _source;
    private readonly object _generation;
    public SnapshotCompletionProjection Completion { get; }

    internal SnapshotCompletionPermit(RecoveryIndex source, object generation, SnapshotCompletionProjection completion)
        { _source = source; _generation = generation; Completion = completion; }

    internal bool IsAuthorized(IRecoveryIndex source, RecoveryProjection projection, IReadOnlyList<ResourceClaim> claims)
    {
        if (!ReferenceEquals(source, _source) || !projection.IsReady ||
            !_source.IsCurrentCompletion(_generation, Completion, projection) || claims.Count != Completion.SlotKeys.Length ||
            claims.Any(claim => claim.Mode != ResourceClaimMode.Mutation || !Completion.SlotKeys.Contains(claim.ResourceKey, StringComparer.Ordinal)))
            return false;
        var keys = Completion.SlotKeys;
        if (projection.Isolations.Any(isolation => keys.Contains(isolation.ResourceKey, StringComparer.Ordinal) &&
            (isolation.JournalId != Completion.ParentJournalId || isolation.Mode != ResourceClaimMode.Mutation)) ||
            keys.Any(key => !projection.Isolations.Any(isolation => isolation.JournalId == Completion.ParentJournalId &&
                isolation.ResourceKey == key && isolation.Mode == ResourceClaimMode.Mutation))) return false;
        var related = projection.ResourceHolds.Where(hold => keys.Contains(hold.ResourceKey, StringComparer.Ordinal)).ToArray();
        return related.All(hold => hold.Owner == RecoveryHoldOwner.ConfigurationOperations &&
            hold.OperationId == Completion.ParentOperationId && hold.Mode == ResourceClaimMode.Mutation) &&
            keys.All(key => related.Any(hold => hold.ResourceKey == key));
    }
}
