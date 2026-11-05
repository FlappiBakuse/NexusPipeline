using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed class AssetRecoveryHandler(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    Func<Guid, WriterStopObservation> writers) : IJournalRecoveryHandler
{
    public JournalKind Kind => JournalKind.AssetPrepare;

    public IJournalRecoveryWork Prepare(RegisteredJournal journal)
    {
        if (journal.Kind != Kind) throw new IOException("asset_recovery_kind_unbound");
        return new Work(new(instance, contracts, database, journal, writers));
    }

    private sealed class Work(AssetRecoverySession session) : IJournalRecoveryWork
    {
        public JournalRecoveryResult Execute() => session.Recover();
        public void Dispose() => session.Dispose();
    }
}
