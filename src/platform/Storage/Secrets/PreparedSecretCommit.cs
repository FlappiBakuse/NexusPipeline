using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class PreparedSecretCommit(SecretPreparationPlan wire, RegisteredJournal cursor,
    JournalDocument committed, string completedReceiptDigest, SecretPreparationEvidence evidence) : IDisposable
{
    internal SecretPreparationPlan Wire { get; } = wire;
    internal RegisteredJournal Cursor { get; } = cursor;
    internal JournalDocument Committed { get; } = committed;
    internal string CompletedReceiptDigest { get; } = completedReceiptDigest;

    internal static PreparedSecretCommit Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts, BlobReference reference)
    {
        var frozen = new PreparationPlanCodec(instance, contracts, database).Read(reference);
        var plan = frozen.ReadWire();
        var journal = new SecretPreparationJournal(instance, contracts, database, frozen, reference);
        var cursor = journal.Registered(); var physical = journal.ReadOptional();
        if (physical is not { Phase: JournalPhase.Committed } || SecretPreparationJournal.NoEffects(physical) ||
            cursor.Phase != JournalPhase.Applied || physical.Revision != cursor.Revision + 1 ||
            new JournalEffectGate(database).Read(cursor.JournalId).State != EffectGateState.Entered)
            throw new IOException("secret_child_not_committed");
        var evidence = new PreparationEvidenceStore(instance, contracts, database).Read(frozen, reference, inspectCandidate: true);
        try
        {
            if (!evidence.CandidateVerified || evidence.Completed is null || evidence.Started is null)
                throw new IOException("secret_child_candidate_unverified");
            var effect = new FileEffectOutbox(database).Read(cursor.JournalId).Single();
            if (effect.State != FileEffectState.Completed || effect.ReceiptDigest != evidence.Completed.Digest ||
                effect.IntentDigest != reference.Sha256 || effect.StepId != "secret-payload" || effect.Action != FileEffectAction.InstallCandidate)
                throw new IOException("secret_child_receipt_unbound");
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System);
            using var transaction = connection.BeginTransaction(deferred: true);
            SecretPreparationRows.VerifyPlan(connection, transaction, plan);
            if (SecretPreparationRows.Read(connection, transaction, Guid.Parse(plan.OperationId), plan.ReplayProof) != SecretPreparationState.Pending)
                throw new IOException("secret_child_state_unbound");
            transaction.Commit();
            return new(plan, cursor, physical, evidence.Completed.Digest, evidence);
        }
        catch { evidence.Dispose(); throw; }
    }

    internal static SecretPayloadIdentity Publish(StateDatabase database, SqliteConnection connection, SqliteTransaction transaction,
        SecretPreparationPlan plan, RegisteredJournal cursor, JournalDocument committed, string completedReceiptDigest)
    {
        SecretPreparationRows.VerifyPlan(connection, transaction, plan);
        if (SecretPreparationRows.Read(connection, transaction, Guid.Parse(plan.OperationId), plan.ReplayProof) != SecretPreparationState.Pending)
            throw new IOException("secret_child_state_unbound");
        var actual = RecoveryJournalRegistry.ReadInside(connection, transaction, cursor.JournalId);
        if (actual.Phase != cursor.Phase || actual.Revision != cursor.Revision || actual.OperationId != cursor.OperationId ||
            actual.PlanPath != cursor.PlanPath || actual.PlanDigest != cursor.PlanDigest || !actual.ResourceKeys.SequenceEqual(cursor.ResourceKeys))
            throw new IOException("secret_child_cursor_changed");
        JournalEffectGate.Require(connection, transaction, actual.JournalId, EffectGateState.Entered);
        var effect = FileEffectRows.Read(connection, actual.JournalId, transaction).Single();
        if (effect.State != FileEffectState.Completed || effect.IntentDigest != actual.PlanDigest ||
            effect.ReceiptDigest != completedReceiptDigest || effect.StepId != "secret-payload" || effect.Action != FileEffectAction.InstallCandidate)
            throw new IOException("secret_child_receipt_sql_changed");
        SecretPreparationRows.SetState(connection, transaction, plan, SecretPreparationState.Published);
        new RecoveryJournalRegistry(database).AdvanceInside(connection, transaction,
            actual with { Phase = JournalPhase.Committed, Revision = committed.Revision }, actual.Revision);
        var candidate = plan.Candidate;
        return new(Guid.Parse(candidate.SecretId), candidate.ProtectionScheme, candidate.OwnerSid, candidate.Sha256, candidate.SizeBytes);
    }

    public void Dispose() => evidence.Dispose();
}
