using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;

namespace NexusPipeline.Core.Extensions;

public enum ExtensionApplyOutcome { Pending, NotExecuted, Installed, Removed, OriginalRestored }

public sealed record ExtensionApplyState(OperationSnapshot Operation, string ExtensionId, JournalPhase Phase,
    ExtensionApplyOutcome Outcome, long? ResultInstalledRevision, bool? ResultIsInstalled, Guid? HistoricalActivationId)
{
    public ExtensionApplyAction Action { get; init; }
}

public sealed record ExtensionOperationItem(Guid OperationId, DateTimeOffset CreatedAt, string ExtensionId,
    ExtensionApplyAction Action, string Status, ExtensionApplyOutcome Outcome);
public sealed record ExtensionOperationPage(IReadOnlyList<ExtensionOperationItem> Items, int? NextOffset);

public interface IExtensionOperationQueries
{
    ExtensionApplyState? Read(Guid operationId, string subject);
    ExtensionOperationPage List(string subject, int offset, int limit);
}
