using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class PreparedManualSource : IDisposable
{
    private readonly List<IDisposable> _held = [];
    private PreparedNotificationTargets _targets = null!;
    internal NotificationWorkSnapshot Source { get; private set; } = null!;
    internal ImmutableArray<NotificationDeliverySnapshot> Deliveries { get; private set; }

    internal static PreparedManualSource Capture(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AcceptedManualWork actual, NotificationSubmission submission)
    {
        var prepared = new PreparedManualSource();
        try
        {
            ManualWorkState invocation;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                invocation = ManualWorkRows.Read(instance, contracts, connection, transaction, actual.Source.Reference);
                if (invocation.BeginReference is null || invocation.Begin is null || invocation.Result is not null) throw NotificationManualCodec.Invalid();
                transaction.Commit();
            }
            prepared._targets = PreparedNotificationTargets.Capture(instance, database, contracts, submission.NotificationTaskIds);
            prepared._held.Add(prepared._targets); var catalog = prepared._targets.Catalog;
            var blobs = new ImmutableBlobStore(instance, contracts);
            prepared._held.Add(blobs.OpenVerified(invocation.Source.Reference)); prepared._held.Add(blobs.OpenVerified(invocation.BeginReference!));
            var codec = new NotificationManualCodec(instance, contracts);
            prepared.Source = codec.Persist(new(NotificationManualCodec.Format, 1, instance.InstanceId, submission.OperationId,
                actual.Source.Reference, invocation.BeginReference!, actual.Budget.Read().ConsumedMilliseconds, catalog.Reference, catalog.Catalog.Revision,
                submission.Title, submission.Body, prepared._targets.Targets));
            prepared.Deliveries = prepared.Source.Document.Targets.Where(item => item.Disposition == "queued")
                .Select(item => new NotificationDeliveryCodec(instance, contracts).Persist(codec.Delivery(prepared.Source, item))).ToImmutableArray();
            prepared._held.Add(blobs.OpenVerified(prepared.Source.Reference));
            foreach (var delivery in prepared.Deliveries) prepared._held.Add(blobs.OpenVerified(delivery.Reference));
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }

    internal void AcceptInside(SqliteConnection connection, SqliteTransaction transaction, Guid invocation, DateTimeOffset created, Guid generation)
    {
        _targets.RequireCurrent(connection, transaction);
        NotificationManualRows.InsertInside(connection, transaction, Source, invocation, created);
        foreach (var delivery in Deliveries) NotificationDeliveryRows.AcceptExplicitInside(connection, transaction, delivery, created, generation);
    }
    public void Dispose() { foreach (var held in _held) held.Dispose(); _held.Clear(); }
}
