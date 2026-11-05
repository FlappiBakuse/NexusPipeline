using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Journals;

[SupportedOSPlatform("windows")]
internal sealed class SnapshotCompletionEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    PayloadApplyRoots? admissionRoots = null)
    : ISnapshotCompletionEvidence, IRecoveryPreflight
{
    public void VerifyAll(IReadOnlyList<RegisteredJournal> registered)
    {
        if (admissionRoots is null) new JournalRecoveryEvidence(instance, new(instance, contracts), database).VerifyAll(registered);
        else new AdmissionPublicationEvidence(instance, database, contracts, admissionRoots).VerifyAll(registered);
    }

    public void VerifyRecognizedAll(IReadOnlyList<RegisteredJournal> registered)
    {
        new JournalRecoveryEvidence(instance, new(instance, contracts), database).VerifyRecognizedAll(registered);
        new SnapshotCompletionPreflight(instance, database, contracts).Verify(registered);
    }

    public IReadOnlyList<SnapshotCompletionProjection> ReadPendingCompletions(RecoveryRegistrySnapshot verified) =>
        new SnapshotCompletionOwner(instance, database, contracts).ReadPending(verified);
}
