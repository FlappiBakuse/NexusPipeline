using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
public sealed class PreparationRecoveryHandler(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    Func<Guid, WriterStopObservation> writers) : IJournalRecoveryHandler
{
    public JournalKind Kind => JournalKind.SecretPrepare;
    public IJournalRecoveryWork Prepare(RegisteredJournal journal)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, journal.PlanPath));
        var reference = new BlobReference(journal.PlanPath, new FileInfo(path).Length, journal.PlanDigest);
        var frozen = new PreparationPlanCodec(instance, contracts, database).Read(reference);
        var plan = frozen.ReadWire();
        if (plan.JournalId != journal.JournalId.ToString("D") || plan.OperationId != journal.OperationId.ToString("D") ||
            !plan.ResourceKeys.SequenceEqual(journal.ResourceKeys)) throw new IOException("secret_recovery_registration_unbound");
        var parent = NotificationSecretBinding.RequireAccepted(instance, database, contracts, frozen, reference);
        var document = SecretDocumentBinding.Accepted(instance, database, contracts, frozen, reference);
        if (document is not null)
        {
            var publisher = new SecretDocumentPublisher(instance, database, contracts);
            return new SecretBatchRecovery(instance, database, contracts, new(() => publisher.Status(document),
                document.Document.Secrets.Where(change => change.Preparation is not null).Select(change => change.Preparation!).ToArray(),
                () => publisher.Close(document, () => { })), writers);
        }
        return parent is null ? new Work(new(instance, contracts, database,
            journal with { ResourceKeys = Array.AsReadOnly(journal.ResourceKeys.ToArray()) }, writers)) :
            new SecretBatchRecovery(instance, database, contracts, new(
                () => new NotificationPublicationInventory(instance, database, contracts).VerifyOne(parent),
                parent.Document.Secrets.Where(change => change.Disposition == SecretDisposition.Set).Select(change => change.Preparation!).ToArray(),
                () => new SecretPreparationPublication(instance, database).CloseNotification(parent, contracts)), writers);
    }
    private sealed class Work(PreparationRecoverySession session) : IJournalRecoveryWork
    {
        public JournalRecoveryResult Execute() => session.Recover();
        public void Dispose() => session.Dispose();
    }
}
