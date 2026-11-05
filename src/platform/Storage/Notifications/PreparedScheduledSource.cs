using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class PreparedScheduledSource : IDisposable
{
    private readonly List<IDisposable> _held;
    private readonly PreparedNotificationTargets _targets;
    internal NotificationWorkSnapshot Source { get; }
    internal ImmutableArray<NotificationDeliverySnapshot> Deliveries { get; }
    private bool _disposed;

    private PreparedScheduledSource(NotificationWorkSnapshot source,
        ImmutableArray<NotificationDeliverySnapshot> deliveries, PreparedNotificationTargets targets,
        List<IDisposable> held)
    { Source = source; Deliveries = deliveries; _targets = targets; _held = held; }

    internal static PreparedScheduledSource Capture(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AcceptedProviderInvocation actualWork, NotificationSubmission submission)
    {
        var held = new List<IDisposable>();
        try
        {
            ScheduledProviderState invocation;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                invocation = new ScheduledProviderEvidence(instance, contracts).Read(actualWork.Source.Reference, connection, transaction);
                if (invocation.BeginReference is null || invocation.Begin is null || invocation.Close is not null || invocation.Result is not null)
                    throw NotificationScheduledCodec.Invalid();
                transaction.Commit();
            }
            var targets = PreparedNotificationTargets.Capture(instance, database, contracts, submission.NotificationTaskIds); held.Add(targets);
            var catalog = targets.Catalog;
            var blobs = new ImmutableBlobStore(instance, contracts);
            held.Add(blobs.OpenVerified(invocation.Source.Reference)); held.Add(blobs.OpenVerified(invocation.BeginReference!));
            var codec = new NotificationScheduledCodec(instance, contracts);
            var source = codec.Persist(new(NotificationScheduledCodec.Format, 1, instance.InstanceId, submission.OperationId,
                actualWork.Source.Reference, invocation.BeginReference!, actualWork.Budget.Read().ConsumedMilliseconds,
                catalog.Reference, catalog.Catalog.Revision, submission.Title, submission.Body, targets.Targets));
            var deliveries = source.Document.Targets.Where(item => item.Disposition == "queued")
                .Select(item => new NotificationDeliveryCodec(instance, contracts).Persist(codec.Delivery(source, item))).ToImmutableArray();
            held.Add(blobs.OpenVerified(source.Reference));
            foreach (var delivery in deliveries) held.Add(blobs.OpenVerified(delivery.Reference));
            return new(source, deliveries, targets, held);
        }
        catch { foreach (var item in held) item.Dispose(); throw; }
    }

    internal void AcceptInside(SqliteConnection connection, SqliteTransaction transaction, Guid occurrence, DateTimeOffset created, Guid generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = Source.Document;
        _targets.RequireCurrent(connection, transaction);
        NotificationScheduledRows.InsertInside(connection, transaction, Source, occurrence, created);
        foreach (var delivery in Deliveries) NotificationDeliveryRows.AcceptExplicitInside(connection, transaction, delivery, created, generation);
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var held in _held) held.Dispose();
        _held.Clear();
    }
}
