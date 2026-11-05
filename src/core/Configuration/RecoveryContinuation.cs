using System.Collections.Immutable;

namespace NexusPipeline.Core.Configuration;

public sealed class RecoveryContinuation
{
    private readonly RecoveryIndex _source;
    private readonly ImmutableHashSet<string> _resourceKeys;
    public RecoveryHoldOwner Owner { get; }
    public Guid OperationId { get; }

    internal RecoveryContinuation(RecoveryIndex source, RecoveryHoldOwner owner, Guid operationId, IReadOnlySet<string> resourceKeys)
    {
        _source = source; Owner = owner; OperationId = operationId;
        _resourceKeys = resourceKeys.ToImmutableHashSet(StringComparer.Ordinal);
        if (_resourceKeys.Count is < 1 or > 4096 || _resourceKeys.Any(key => string.IsNullOrWhiteSpace(key) ||
            key.Length > 512 || key != key.Trim() || key.Any(char.IsControl)))
            throw new ArgumentException("recovery_continuation_resources_invalid");
    }

    internal bool IsAuthorized(IRecoveryIndex source, RecoveryProjection projection, IReadOnlySet<string> keys)
    {
        if (!ReferenceEquals(source, _source) || !projection.IsReady || keys.Count == 0 || !keys.All(_resourceKeys.Contains) ||
            projection.Isolations.Any(item => keys.Contains(item.ResourceKey))) return false;
        var related = projection.ResourceHolds.Where(item => keys.Contains(item.ResourceKey)).ToArray();
        return related.All(item => item.Owner == Owner && item.OperationId == OperationId) &&
            keys.All(key => related.Any(item => item.ResourceKey == key));
    }
}
