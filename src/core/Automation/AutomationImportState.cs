namespace NexusPipeline.Core.Automation;

public enum AutomationImportState { Accepted, Published, Cancelled, Quarantined }
public sealed record AutomationImportResult(Guid OperationId, Guid? JournalId, AutomationImportState State,
    long? CatalogRevision, string ReasonCode, bool IsNew);
