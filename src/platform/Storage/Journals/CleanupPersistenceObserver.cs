using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Journals;

public enum CleanupPersistencePoint { AcceptanceCommitted, JournalFlushed, SqlProjectionCommitted, FileRemoved, OutboxCommitted }

public interface ICleanupPersistenceObserver
{
    void Reached(CleanupPersistencePoint point, Guid journalId, long revision, JournalPhase phase);
}
