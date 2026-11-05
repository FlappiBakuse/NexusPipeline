using System.Collections.Immutable;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Core.Configuration;

public sealed record ResourceIsolation(string ResourceKey, Guid JournalId, string ReasonCode)
{
    public ResourceClaimMode Mode { get; init; } = ResourceClaimMode.Mutation;
}

public sealed record RecoveryProjection(bool IsReady, long Revision, string? GlobalBlockReason,
    IReadOnlyList<ResourceIsolation> Isolations)
{
    public IReadOnlyList<RecoveryResourceHold> ResourceHolds { get; init; } = [];
    public ImmutableArray<SnapshotCompletionProjection> SnapshotCompletions { get; internal init; } = [];
}
public enum RecoveryHoldOwner { StartupRegistration, ConfigurationOperations, CompletionActions }
public sealed record RecoveryResourceHold(Guid OperationId, string ResourceKey, string ReasonCode)
{
    public RecoveryHoldOwner? Owner { get; init; }
    public ResourceClaimMode Mode { get; init; } = ResourceClaimMode.Mutation;
}

public interface IRecoveryIndex
{
    RecoveryProjection Current { get; }
    RecoveryProjection BlockUnknown(string reasonCode);
}

public sealed class RecoveryIndex : IRecoveryIndex
{
    private readonly object _gate = new();
    private RecoveryProjection _current = new(false, 0, "startup_verification_required", []);
    private readonly Dictionary<RecoveryHoldOwner, IReadOnlyList<RecoveryResourceHold>> _holds = [];
    private object _snapshotCompletionGeneration = new();

    public RecoveryProjection Current => Volatile.Read(ref _current);

    public RecoveryProjection BlockUnknown(string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode)) throw new ArgumentException("A block reason is required");
        lock (_gate)
        {
            _snapshotCompletionGeneration = new();
            var next = new RecoveryProjection(false, checked(_current.Revision + 1), reasonCode, _current.Isolations)
                { ResourceHolds = _current.ResourceHolds };
            Volatile.Write(ref _current, next);
            return next;
        }
    }

    public void PublishVerified(long expectedRevision, long committedRevision, IReadOnlyList<ResourceIsolation> isolations)
        => Publish(expectedRevision, committedRevision, isolations, []);

    internal void PublishVerifiedCompletions(long expectedRevision, long committedRevision,
        IReadOnlyList<ResourceIsolation> isolations, ImmutableArray<SnapshotCompletionProjection> completions)
        => Publish(expectedRevision, committedRevision, isolations, completions);

    private void Publish(long expectedRevision, long committedRevision, IReadOnlyList<ResourceIsolation> isolations,
        ImmutableArray<SnapshotCompletionProjection> completions)
    {
        var frozen = Array.AsReadOnly(isolations.ToArray());
        if (frozen.Any(item => string.IsNullOrWhiteSpace(item.ResourceKey) || item.JournalId == Guid.Empty || string.IsNullOrWhiteSpace(item.ReasonCode) || !Enum.IsDefined(item.Mode)))
            throw new ArgumentException("An isolation must have a verified resource and journal identity");
        lock (_gate)
        {
            if (_current.Revision != expectedRevision || committedRevision <= expectedRevision)
                throw new InvalidOperationException("recovery_index_revision_conflict");
            _snapshotCompletionGeneration = new();
            Volatile.Write(ref _current, new(true, committedRevision, null, frozen)
                { ResourceHolds = _current.ResourceHolds, SnapshotCompletions = completions });
        }
    }

    public bool IsBlocked(IReadOnlySet<string> requestedResourceKeys, out long revision)
    {
        var snapshot = Current;
        revision = snapshot.Revision;
        return !snapshot.IsReady || snapshot.Isolations.Any(item => requestedResourceKeys.Contains(item.ResourceKey))
            || snapshot.ResourceHolds.Any(item => requestedResourceKeys.Contains(item.ResourceKey));
    }

    public void UpdateResourceHolds(RecoveryHoldOwner owner, IReadOnlyList<RecoveryResourceHold> holds)
        => UpdateHolds(owner, holds, null);

    public bool TryUpdateHolds(RecoveryHoldOwner owner, long expectedRevision, IReadOnlyList<RecoveryResourceHold> holds)
        => UpdateHolds(owner, holds, expectedRevision);

    private bool UpdateHolds(RecoveryHoldOwner owner, IReadOnlyList<RecoveryResourceHold> holds, long? expectedRevision)
    {
        if (!Enum.IsDefined(owner)) throw new ArgumentException("recovery_hold_owner_invalid");
        var frozen = Array.AsReadOnly(holds.ToArray());
        if (frozen.Count > 4096 || frozen.Any(item => item is null || item.OperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(item.ResourceKey) || item.ResourceKey.Length > 512 || item.ResourceKey.Any(char.IsControl) ||
            !Enum.IsDefined(item.Mode) ||
            string.IsNullOrWhiteSpace(item.ReasonCode) || item.ReasonCode.Length > 80 || item.ReasonCode.Any(char.IsControl)) ||
            frozen.Select(item => (item.OperationId, item.ResourceKey)).Distinct().Count() != frozen.Count)
            throw new ArgumentException("recovery_resource_holds_invalid");
        var owned = Array.AsReadOnly(frozen.Select(item => item with { Owner = owner }).OrderBy(item => item.OperationId)
            .ThenBy(item => item.ResourceKey, StringComparer.Ordinal).ToArray());
        lock (_gate)
        {
            if (expectedRevision.HasValue && _current.Revision != expectedRevision.Value) return false;
            // An empty first registration changes no resource state and must not invalidate a close fence.
            if (owned.Count == 0 && !_holds.ContainsKey(owner)) return true;
            if (_holds.TryGetValue(owner, out var current) && current.SequenceEqual(owned)) return true;
            _holds[owner] = owned;
            var combined = Array.AsReadOnly(_holds.OrderBy(item => item.Key).SelectMany(item => item.Value).ToArray());
            Volatile.Write(ref _current, _current with { Revision = checked(_current.Revision + 1), ResourceHolds = combined });
            return true;
        }
    }

    public RecoveryContinuation? AuthorizeContinuation(RecoveryHoldOwner owner, Guid operationId, IReadOnlySet<string> resourceKeys)
    {
        if (!Enum.IsDefined(owner) || operationId == Guid.Empty) throw new ArgumentException("recovery_continuation_identity_invalid");
        ArgumentNullException.ThrowIfNull(resourceKeys);
        var continuation = new RecoveryContinuation(this, owner, operationId, resourceKeys);
        return continuation.IsAuthorized(this, Current, resourceKeys) ? continuation : null;
    }

    public SnapshotCompletionPermit? AuthorizeSnapshotCompletion(Guid parentJournalId, Guid sessionId, Guid completionOperationId)
    {
        if (parentJournalId == Guid.Empty || sessionId == Guid.Empty || completionOperationId == Guid.Empty)
            throw new ArgumentException("snapshot_completion_identity_invalid");
        lock (_gate)
        {
            var completion = _current.SnapshotCompletions.SingleOrDefault(item => item.ParentJournalId == parentJournalId &&
                item.SessionId == sessionId && item.CompletionOperationId == completionOperationId);
            if (completion is null) return null;
            var permit = new SnapshotCompletionPermit(this, _snapshotCompletionGeneration, completion);
            var claims = completion.SlotKeys.Select(key => new ResourceClaim(key, ResourceClaimMode.Mutation)).ToArray();
            return permit.IsAuthorized(this, _current, claims) ? permit : null;
        }
    }

    internal bool IsCurrentCompletion(object generation, SnapshotCompletionProjection completion, RecoveryProjection projection)
    {
        lock (_gate) return ReferenceEquals(_snapshotCompletionGeneration, generation) && ReferenceEquals(_current, projection) &&
            _current.SnapshotCompletions.Any(item => ReferenceEquals(item, completion));
    }
}
