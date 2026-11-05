using System.Collections.Immutable;

namespace NexusPipeline.Core.Configuration;

public sealed record ConfigurationRevisionBatch(ImmutableArray<ConfigurationRevisionChange> Changes);
public sealed record ConfigurationBatchResult(Guid OperationId, ImmutableArray<ConfigurationSaveResult> Items, bool IsNew);

public static class RevisionBatchPolicy
{
    public static ConfigurationRevisionBatch Freeze(ConfigurationRevisionBatch batch)
    {
        if (batch is null || batch.Changes.IsDefaultOrEmpty || batch.Changes.Length > 32)
            throw new ConfigurationException("configuration_batch_invalid");
        var changes = batch.Changes.Select(ConfigurationRevisionPolicy.Freeze).OrderBy(change => change.Address.ResourceId,
            StringComparer.Ordinal).ToImmutableArray();
        if (changes.Select(change => change.Address).Distinct().Count() != changes.Length ||
            changes.Select(change => change.Address.BindingId).Distinct().Count() != 1 ||
            changes.Select(change => change.ExpectedBindingRevision).Distinct().Count() != 1)
            throw new ConfigurationException("configuration_batch_owner_invalid");
        return new(changes);
    }
}
