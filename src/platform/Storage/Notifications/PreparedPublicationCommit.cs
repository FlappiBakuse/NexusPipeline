using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

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
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
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
