namespace NexusPipeline.Core.Configuration;

public interface IRecoveryRegistry
{
    RecoveryRegistrySnapshot ReadSnapshot();
    RecoveryRegistrySnapshot PublishVerified(long expectedRevision, long minimumRevision);
}

public interface IRecoveryEvidence
{
    void VerifyAll(IReadOnlyList<RegisteredJournal> registered);
}

public interface ISnapshotCompletionEvidence : IRecoveryEvidence
{
    IReadOnlyList<SnapshotCompletionProjection> ReadPendingCompletions(RecoveryRegistrySnapshot verified);
}

public interface IRecoveryPreflight
{
    void VerifyRecognizedAll(IReadOnlyList<RegisteredJournal> registered);
}

public interface IRecoveryExecutionOwnership
{
    IDisposable Begin(IReadOnlyList<RegisteredJournal> journals);
}

public enum JournalRecoveryResult { NotExecuted, Restored, Quarantined, AlreadyCompleted, Completed, PendingConfigurationCompletion }

public interface IJournalRecoveryWork : IDisposable
{
    IReadOnlyList<Guid> MustRunAfter => Array.Empty<Guid>();
    JournalRecoveryResult Execute();
    void IDisposable.Dispose() { }
}

public interface IJournalRecoveryHandler
{
    JournalKind Kind { get; }
    IJournalRecoveryWork Prepare(RegisteredJournal journal);
}
