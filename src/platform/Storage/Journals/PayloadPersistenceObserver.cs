using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Journals;

public enum PayloadPersistencePoint { AcceptanceCommitted, JournalCreated, GateSealed, JournalFlushed, OutboxCommitted, SqlProjectionCommitted, PhysicalTreeMoved }
public interface IPayloadPersistenceObserver
{
    void Reached(PayloadPersistencePoint point, Guid journalId, long revision, JournalPhase phase);
}
