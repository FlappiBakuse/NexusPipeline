using System.Runtime.Versioning;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class NotificationCatalogAcceptance(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal bool Accept(PreparedNotificationPublication prepared, DateTimeOffset acceptedAt)
    {
        var publication = new NotificationPublicationCodec(instance, contracts).Read(prepared.Publication.Reference);
        if (publication.Document.InstanceId != database.InstanceId || publication.Reference != prepared.Publication.Reference)
            throw new IOException("notification_publication_instance_unbound");
        var payloads = prepared.Payloads.Select(payload =>
        {
            var plan = payload.Plan.ReadWire();
            var field = publication.Document.Secrets.Single(item => item.OperationId.ToString("D") == plan.OperationId);
            if (field.Preparation != payload.Reference || field.JournalId?.ToString("D") != plan.JournalId)
                throw new IOException("notification_publication_child_unbound");
            var key = SecretPreparationRows.Key(plan);
            var operation = SecretFieldStore.Identity(field.OperationId, key, field.Before.Revision, SecretDisposition.Set);
            _ = new SecretPreparationAcceptance(database).RequirePrepared(operation, payload);
            if (plan.NotificationPublication is not { } binding || binding.ParentOperationId != publication.Document.OperationId.ToString("D") ||
                binding.ParentRequestDigest != publication.Document.RequestDigest ||
                key.SubjectId != publication.Document.Request.TaskId || key.OwnerInstanceId != instance.InstanceId)
                throw new IOException("notification_publication_child_parent_unbound");
            return (Payload: payload, Plan: plan, Operation: operation);
        }).ToArray();
        if (payloads.Length != publication.Document.Secrets.Count(item => item.Disposition == SecretDisposition.Set))
            throw new IOException("notification_publication_child_coverage_invalid");
        var writer = JournalWriterStore.CurrentWriter;
        return database.Write((connection, transaction) =>
        {
            if (!NotificationCatalogRows.AcceptInside(connection, transaction, publication, acceptedAt)) return false;
            foreach (var field in publication.Document.Secrets)
            {
                var key = SecretFieldKey.NotificationTask(instance.InstanceId, publication.Document.Request.TaskId!.Value, field.Field);
                var operation = SecretFieldStore.Identity(field.OperationId, key, field.Before.Revision, field.Disposition);
                if (field.Disposition == SecretDisposition.Set)
                {
                    var child = payloads.Single(item => item.Operation.OperationId == field.OperationId);
                    if (!SecretPreparationAcceptance.AcceptInside(connection, transaction, operation, child.Plan, child.Payload.Reference, writer))
                        throw new IOException("notification_child_operation_reused");
                }
                else if (!OperationRows.Accept(connection, transaction, operation, acceptedAt).IsNew)
                    throw new IOException("notification_child_operation_reused");
            }
            return true;
        });
    }
}
