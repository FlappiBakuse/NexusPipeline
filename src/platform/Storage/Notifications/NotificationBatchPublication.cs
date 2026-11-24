using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Runtime.Versioning;

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

internal sealed record NotificationCommittedChild(NotificationSecretChange Change, SecretPreparationPlan Wire,
    RegisteredJournal Cursor, JournalDocument Committed, string CompletedReceiptDigest);

[SupportedOSPlatform("windows")]
internal sealed class PreparedPublicationCommit : IDisposable
{
    private readonly List<IDisposable> _held = [];
    internal NotificationPublicationSnapshot Publication { get; }
    internal ImmutableArray<NotificationCommittedChild> Children { get; private set; }
    internal ImmutableArray<SecretFieldRetirement> Retirements { get; private set; }

    private PreparedPublicationCommit(NotificationPublicationSnapshot publication) => Publication = publication;

    internal static PreparedPublicationCommit Read(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, NotificationPublicationSnapshot publication)
    {
        publication = new NotificationPublicationCodec(instance, contracts).Read(publication.Reference);
        var proof = new PreparedPublicationCommit(publication);
        try
        {
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System))
            using (var transaction = DatabaseConnections.BeginRead(connection))
                NotificationCatalogRows.RequirePendingInside(connection, transaction, publication);
            var children = ImmutableArray.CreateBuilder<NotificationCommittedChild>();
            var retirements = ImmutableArray.CreateBuilder<SecretFieldRetirement>();
            foreach (var change in publication.Document.Secrets)
            {
                if (change.Retirement is { })
                {
                    var retirement = SecretFieldRetirement.FromNotification(instance, change.OperationId, change.Before.SecretId, change.Retirement)!;
                    proof._held.Add(retirement); retirements.Add(retirement);
                }
                if (change.Disposition != SecretDisposition.Set) continue;
                var child = PreparedSecretCommit.Read(instance, database, contracts, change.Preparation!);
                proof._held.Add(child);
                children.Add(new(change, child.Wire, child.Cursor, child.Committed, child.CompletedReceiptDigest));
            }
            proof.Children = children.ToImmutable(); proof.Retirements = retirements.ToImmutable();
            return proof;
        }
        catch { proof.Dispose(); throw; }
    }

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }
}
