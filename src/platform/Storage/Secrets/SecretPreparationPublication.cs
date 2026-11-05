using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class SecretPreparationPublication(RuntimeInstance instance, StateDatabase database)
{
    internal void CloseNotification(NotificationPublicationSnapshot publication, ContractReader contracts) =>
        new NotificationBatchPublication(instance, database, contracts).Close(publication);

    internal SecretFieldStatus? Close(FrozenSecretPreparation frozen, BlobReference reference, JournalDocument committed, bool cancelled)
    {
        var plan = frozen.ReadWire();
        if (plan.NotificationPublication is not null || plan.DocumentPublication is not null)
            throw new IOException("secret_parent_publication_required");
        if (committed.Kind != JournalKind.SecretPrepare || committed.Phase != JournalPhase.Committed || committed.JournalId.ToString("D") != plan.JournalId ||
            committed.PlanDigest != reference.Sha256 || SecretPreparationJournal.NoEffects(committed) != cancelled)
            throw new IOException("secret_publication_commit_unbound");
        using var retirement = cancelled ? null : SecretFieldRetirement.FromFrozen(instance, plan);
        var result = database.Write((connection, transaction) =>
        {
            var state = SecretPreparationRows.Read(connection, transaction, Guid.Parse(plan.OperationId), plan.ReplayProof);
            if (state != SecretPreparationState.Pending) throw new IOException("secret_publication_state_unbound");
            SecretPreparationRows.VerifyBefore(connection, transaction, plan);
            SecretFieldStatus? published = null;
            if (!cancelled)
            {
                var c = plan.Candidate;
                var next = new SecretFieldReference(plan.ExpectedRevision + 1, Guid.Parse(c.SecretId));
                SecretFieldStore.WriteResult(connection, transaction, Guid.Parse(plan.OperationId), SecretPreparationRows.Key(plan), next,
                    new(Guid.Parse(c.SecretId), c.ProtectionScheme, c.OwnerSid, c.Sha256, c.SizeBytes), true, plan.ReplayProof);
                retirement?.RegisterInside(connection, transaction);
                published = new(next.Revision, next.SecretId, true, true);
            }
            SecretPreparationRows.SetState(connection, transaction, plan, cancelled ? SecretPreparationState.Cancelled : SecretPreparationState.Published);
            var registered = RecoveryJournalRegistry.ReadInside(connection, transaction, Guid.Parse(plan.JournalId));
            new RecoveryJournalRegistry(database).AdvanceInside(connection, transaction,
                registered with { Phase = JournalPhase.Committed, Revision = committed.Revision }, registered.Revision,
                cancelled ? new(true, false, false, false) : null);
            return published;
        });
        retirement?.Materialize(database);
        return result;
    }
}
