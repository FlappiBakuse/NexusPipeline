using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Execution;

public enum AttemptPersistencePoint { AcceptanceCommitted, JournalCreated, GateSealed, JournalFlushed, AttemptReceiptCommitted, SqlProjectionCommitted }

public interface IAttemptPersistenceObserver
{
    void Reached(AttemptPersistencePoint point, Guid journalId, long revision, JournalPhase phase);
}
