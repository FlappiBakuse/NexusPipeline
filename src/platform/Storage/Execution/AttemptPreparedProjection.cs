using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AttemptPreparedProjection(JournalStore store, RecoveryJournalRegistry registry, AttemptReceiptStore receipts, IAttemptPersistenceObserver? observer)
{
    internal void Synchronize(RunAttemptPlan plan, bool verifiedNoEffects)
    {
        _ = receipts;
        var state = AttemptWriterState.Read(store.Instance, registry.Database, store.Contracts, Guid.Parse(plan.JournalId));
        ExecutionAttemptProjection.Synchronize(store.Instance, registry.Database, store.Contracts, state, verifiedNoEffects, observer);
    }
}
