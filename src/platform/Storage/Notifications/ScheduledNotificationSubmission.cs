using System.Runtime.Versioning;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class ScheduledNotificationSubmission(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, Func<NotificationSubmission, WorkNotificationOrigin> bind, NotificationRuntime runtime)
    : INotificationSubmissionAuthorization, INotificationSubmissionQueue
{
    public void AssertAllowed(NotificationSubmission submission)
    {
        var work = Require(submission);
        lease.PublishExecution(() =>
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
            using var transaction = DatabaseConnections.BeginRead(connection);
            work.RequireCurrent(connection, transaction);
        });
    }

    public ValueTask<NotificationQueueAcceptance> AcceptAsync(NotificationSubmission submission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var work = Require(submission);
        return new(work.Use(() =>
        {
            lease.VerifyHeld(instance); cancellationToken.ThrowIfCancellationRequested();
            var prior = lease.PublishExecution(() => ReadExisting(submission, work));
            if (prior is not null) return prior;
            using var prepared = PreparedScheduledSource.Capture(instance, database, contracts, work, submission);
            using var activity = runtime.PrepareWork(work.Activity);
            return lease.PublishExecution(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var replay = ReadExisting(submission, work);
                if (replay is not null) return replay;
                database.Write((connection, transaction) =>
                {
                    work.RequireCurrent(connection, transaction);
                    prepared.AcceptInside(connection, transaction, work.OperationId, DateTimeOffset.UtcNow, lease.StartupGeneration);
                    return true;
                });
                activity.CommittedScheduled(prepared.Source.Reference);
                return new NotificationQueueAcceptance(submission.OperationId, prepared.Source.Document.OperationId, true);
            });
        }));
    }

    private WorkNotificationOrigin Require(NotificationSubmission submission)
    {
        lease.VerifyHeld(instance);
        return bind(submission);
    }

    private NotificationQueueAcceptance? ReadExisting(NotificationSubmission submission, WorkNotificationOrigin work)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.System);
        using var transaction = DatabaseConnections.BeginRead(connection);
        work.RequireCurrent(connection, transaction);
        var reference = NotificationScheduledRows.Lookup(connection, transaction, submission.OperationId);
        if (reference is null)
        {
            using var occupied = NotificationDeliveryRows.Command(connection, transaction,
                "SELECT 1 FROM main.operations WHERE operation_id=$0;", submission.OperationId.ToString("D"));
            if (occupied.ExecuteScalar() is not null) throw NotificationScheduledCodec.Invalid();
            return null;
        }
        var source = new NotificationScheduledCodec(instance, contracts).Read(reference, connection, transaction);
        if (source.Document.Invocation != work.Reference || source.Document.Title != submission.Title ||
            source.Document.Body != submission.Body || !source.Document.Targets.Select(item => item.TaskId).SequenceEqual(submission.NotificationTaskIds))
            throw NotificationScheduledCodec.Invalid();
        transaction.Commit();
        return new(submission.OperationId, source.Document.OperationId, false);
    }
}

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
        WorkNotificationOrigin actualWork, NotificationSubmission submission)
    {
        var held = new List<IDisposable>();
        try
        {
            WorkStartEvidence begin;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.System))
            using (var transaction = DatabaseConnections.BeginRead(connection))
            {
                var invocation = WorkInvocationEvidence.ReadScheduled(instance, contracts, actualWork.Reference, connection, transaction);
                if (!invocation.IsOpen || invocation.Begin is not { } started) throw NotificationScheduledCodec.Invalid();
                begin = started;
                transaction.Commit();
            }
            var targets = PreparedNotificationTargets.Capture(instance, database, contracts, submission.NotificationTaskIds); held.Add(targets);
            var catalog = targets.Catalog;
            var blobs = new ImmutableBlobStore(instance, contracts);
            held.Add(blobs.OpenVerified(actualWork.Reference)); held.Add(blobs.OpenVerified(begin.Reference));
            var codec = new NotificationScheduledCodec(instance, contracts);
            var source = codec.Persist(new(NotificationScheduledCodec.Format, 1, instance.InstanceId, submission.OperationId,
                actualWork.Reference, begin.Reference, actualWork.ReadConsumedMilliseconds(),
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
