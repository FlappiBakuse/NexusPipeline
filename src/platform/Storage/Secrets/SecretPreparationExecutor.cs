using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Secrets;

public enum SecretPreparationPoint { Accepted, InitialJournal, EffectEntered, Applying, StartedReceipt, StartedOutbox,
    CiphertextFlushed, CompletedReceipt, CompletedOutbox, Applied, CommittedJournal, Published }
public interface ISecretPreparationObserver { void Reached(SecretPreparationPoint point, Guid journalId); }

[SupportedOSPlatform("windows")]
internal sealed class SecretPreparationExecutor(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    ISecretPreparationObserver? observer = null)
{
    internal SecretFieldStatus Execute(OperationIdentity operation, PreparedSecretPayload draft)
    {
        var plan = draft.Plan.ReadWire(); var id = Guid.Parse(plan.JournalId);
        if (!new SecretPreparationAcceptance(database).Accept(operation, draft))
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System);
            return SecretFieldStore.ReadReplay(connection, null, operation, plan.ReplayProof)
                ?? throw new IOException("secret_preparation_original_result_missing");
        }
        observer?.Reached(SecretPreparationPoint.Accepted, id);
        var terminal = WriteAcceptedCandidate(draft);
        var result = new SecretPreparationPublication(instance, database).Close(draft.Plan, draft.Reference, terminal, cancelled: false)
            ?? throw new IOException("secret_preparation_result_missing");
        observer?.Reached(SecretPreparationPoint.Published, id);
        return result;
    }

    internal JournalDocument WriteAcceptedCandidate(PreparedSecretPayload draft)
    {
        var plan = draft.Plan.ReadWire(); var id = Guid.Parse(plan.JournalId);
        _ = new JournalWriterStore(database).RequireCurrentExecutor(id);
        _ = NotificationSecretBinding.RequireAccepted(instance, database, contracts, draft.Plan, draft.Reference);
        _ = SecretDocumentBinding.Accepted(instance, database, contracts, draft.Plan, draft.Reference);
        var journal = new SecretPreparationJournal(instance, contracts, database, draft.Plan, draft.Reference);
        journal.Materialize(); At(SecretPreparationPoint.InitialJournal);
        new JournalEffectGate(database).Enter(journal.Registered(), 1); At(SecretPreparationPoint.EffectEntered);
        journal.Append(JournalPhase.Applying); journal.ProjectNonterminal(); At(SecretPreparationPoint.Applying);
        var evidence = new PreparationEvidenceStore(instance, contracts, database);
        evidence.VerifyParent(plan, receipts: false);
        var started = evidence.Write(draft.Plan, draft.Reference, "started", null); At(SecretPreparationPoint.StartedReceipt);
        new FileEffectOutbox(database).Record(id, "secret-payload", FileEffectState.Pending, FileEffectState.Started, started.Digest);
        At(SecretPreparationPoint.StartedOutbox);
        var path = evidence.CandidatePath(plan);
        PhysicalObjectIdentity identity;
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            output.Write(draft.Ciphertext); output.Flush(flushToDisk: true); identity = PhysicalObjectIdentity.Read(output.SafeFileHandle);
        }
        At(SecretPreparationPoint.CiphertextFlushed);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (PhysicalObjectIdentity.Read(held.SafeFileHandle) != identity || held.Length != plan.Candidate.SizeBytes ||
            Convert.ToHexStringLower(SHA256.HashData(held)) != plan.Candidate.Sha256) throw new IOException("secret_preparation_candidate_changed");
        var completed = evidence.Write(draft.Plan, draft.Reference, "completed", identity); At(SecretPreparationPoint.CompletedReceipt);
        new FileEffectOutbox(database).Record(id, "secret-payload", FileEffectState.Started, FileEffectState.Completed, completed.Digest);
        At(SecretPreparationPoint.CompletedOutbox);
        using var verified = evidence.Read(draft.Plan, draft.Reference, inspectCandidate: true);
        if (!verified.CandidateVerified) throw new IOException("secret_preparation_candidate_unverified");
        journal.Append(JournalPhase.Applied); journal.ProjectNonterminal(); At(SecretPreparationPoint.Applied);
        var terminal = journal.Append(JournalPhase.Committed); At(SecretPreparationPoint.CommittedJournal);
        return terminal;
        void At(SecretPreparationPoint point) => observer?.Reached(point, id);
    }
}
