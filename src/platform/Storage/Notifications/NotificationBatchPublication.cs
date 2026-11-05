using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class NotificationBatchPublication(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal void Publish(PreparedPublicationCommit proof)
    {
        var publication = proof.Publication;
        database.Write((connection, transaction) =>
        {
            NotificationCatalogRows.RequirePendingInside(connection, transaction, publication);
            foreach (var change in publication.Document.Secrets)
            {
                var key = SecretFieldKey.NotificationTask(instance.InstanceId, publication.Document.Request.TaskId!.Value, change.Field);
                var operation = SecretFieldStore.Identity(change.OperationId, key, change.Before.Revision, change.Disposition);
                var accepted = OperationRows.ReadExisting(connection, transaction, operation);
                if (accepted is null || accepted.Status != "accepted") throw new IOException("notification_child_operation_unbound");
                CoreSecretCandidate(change);
                void CoreSecretCandidate(NotificationSecretChange value)
                {
                    SecretPayloadIdentity? candidate = null;
                    if (value.Disposition == SecretDisposition.Set)
                    {
                        var child = proof.Children.Single(item => item.Change == value);
                        var plan = child.Wire;
                        candidate = PreparedSecretCommit.Publish(database, connection, transaction,
                            plan, child.Cursor, child.Committed, child.CompletedReceiptDigest);
                    }
                    SecretFieldStore.WriteResult(connection, transaction, value.OperationId, key, value.After,
                        candidate, value.ResultReadable, value.ReplayProof);
                }
            }
            foreach (var retirement in proof.Retirements) retirement.RegisterInside(connection, transaction);
            NotificationCatalogRows.CommitCatalogInside(connection, transaction, publication);
            return true;
        });
        foreach (var retirement in proof.Retirements) retirement.Materialize(database);
    }

    internal void Close(NotificationPublicationSnapshot publication)
    {
        publication = new NotificationPublicationCodec(instance, contracts).Read(publication.Reference);
        if (new NotificationPublicationInventory(instance, database, contracts).VerifyOne(publication) == "completed") return;
        using var proof = PreparedPublicationCommit.Read(instance, database, contracts, publication);
        Publish(proof);
    }
}
