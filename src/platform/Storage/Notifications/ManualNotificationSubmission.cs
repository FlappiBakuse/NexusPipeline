using System.Runtime.Versioning;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class ManualNotificationSubmission(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, Func<NotificationSubmission, WorkNotificationOrigin> bind, NotificationRuntime runtime)
    : INotificationSubmissionAuthorization, INotificationSubmissionQueue
{
    public void AssertAllowed(NotificationSubmission submission)
    {
        var work = Require(submission);
        lease.PublishExecution(() =>
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
            using var transaction = DatabaseConnections.BeginRead(connection); work.RequireCurrent(connection, transaction);
        });
    }

    public ValueTask<NotificationQueueAcceptance> AcceptAsync(NotificationSubmission submission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var work = Require(submission);
        return new(work.Use(() =>
        {
            lease.VerifyHeld(instance); cancellationToken.ThrowIfCancellationRequested();
            var prior = lease.PublishExecution(() => ReadExisting(submission, work)); if (prior is not null) return prior;
            using var prepared = PreparedManualSource.Capture(instance, database, contracts, work, submission);
            using var activity = runtime.PrepareWork(work.Activity);
            return lease.PublishExecution(() =>
            {
                cancellationToken.ThrowIfCancellationRequested(); var replay = ReadExisting(submission, work); if (replay is not null) return replay;
                database.Write((connection, transaction) =>
                {
                    work.RequireCurrent(connection, transaction); cancellationToken.ThrowIfCancellationRequested();
                    prepared.AcceptInside(connection, transaction, work.OperationId, DateTimeOffset.UtcNow, lease.StartupGeneration); return true;
                });
                activity.CommittedManual(prepared.Source.Reference);
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
        using var transaction = DatabaseConnections.BeginRead(connection); work.RequireCurrent(connection, transaction);
        var reference = NotificationManualRows.Lookup(connection, transaction, submission.OperationId);
        if (reference is null)
        {
            using var occupied = NotificationDeliveryRows.Command(connection, transaction,
                "SELECT 1 FROM main.operations WHERE operation_id=$0;", submission.OperationId.ToString("D"));
            if (occupied.ExecuteScalar() is not null) throw NotificationManualCodec.Invalid(); return null;
        }
        var source = new NotificationManualCodec(instance, contracts).Read(reference, connection, transaction);
        if (source.Document.Invocation != work.Reference || source.Document.Title != submission.Title || source.Document.Body != submission.Body ||
            !source.Document.Targets.Select(item => item.TaskId).SequenceEqual(submission.NotificationTaskIds)) throw NotificationManualCodec.Invalid();
        transaction.Commit(); return new(submission.OperationId, source.Document.OperationId, false);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class PreparedManualSource : IDisposable
{
    private readonly List<IDisposable> _held = [];
    private PreparedNotificationTargets _targets = null!;
    internal NotificationWorkSnapshot Source { get; private set; } = null!;
    internal ImmutableArray<NotificationDeliverySnapshot> Deliveries { get; private set; }

    internal static PreparedManualSource Capture(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        WorkNotificationOrigin actual, NotificationSubmission submission)
    {
        var prepared = new PreparedManualSource();
        try
        {
            WorkStartEvidence begin;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.System))
            using (var transaction = DatabaseConnections.BeginRead(connection))
            {
                var invocation = WorkInvocationEvidence.ReadManual(instance, contracts, actual.Reference, connection, transaction);
                if (!invocation.IsOpen || invocation.Begin is not { } started) throw NotificationManualCodec.Invalid();
                begin = started;
                transaction.Commit();
            }
            prepared._targets = PreparedNotificationTargets.Capture(instance, database, contracts, submission.NotificationTaskIds);
            prepared._held.Add(prepared._targets); var catalog = prepared._targets.Catalog;
            var blobs = new ImmutableBlobStore(instance, contracts);
            prepared._held.Add(blobs.OpenVerified(actual.Reference)); prepared._held.Add(blobs.OpenVerified(begin.Reference));
            var codec = new NotificationManualCodec(instance, contracts);
            prepared.Source = codec.Persist(new(NotificationManualCodec.Format, 1, instance.InstanceId, submission.OperationId,
                actual.Reference, begin.Reference, actual.ReadConsumedMilliseconds(), catalog.Reference, catalog.Catalog.Revision,
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
