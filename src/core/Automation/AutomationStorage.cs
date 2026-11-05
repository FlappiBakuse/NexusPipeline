using NexusPipeline.Core.Operations;
using System.Collections.Immutable;

namespace NexusPipeline.Core.Automation;

public sealed record AutomationChangeResult(Guid OperationId, long CatalogRevision, bool IsNew)
{
    public ImmutableArray<Guid> CleanupJournalIds { get; init; } = [];
}

public interface IAutomationDefinitionStore
{
    AutomationCatalog Read();
    AutomationChangeResult Commit(OperationIdentity operation, AutomationChanges changes, DateTimeOffset acceptedAt);
}
