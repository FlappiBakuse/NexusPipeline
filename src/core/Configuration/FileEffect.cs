namespace NexusPipeline.Core.Configuration;

public enum FileEffectAction { PreserveOriginal, InstallCandidate, RetireCandidate, RestoreOriginal, RemoveOwned }
public enum FileEffectState { Pending, Started, Completed, NotExecuted, Quarantined, Reverted }

public sealed record FileEffect(Guid JournalId, string StepId, FileEffectAction Action, FileEffectState State,
    string IntentDigest, string? ReceiptDigest);
