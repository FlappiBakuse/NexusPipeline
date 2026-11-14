using System.Runtime.Versioning;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

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
            using var transaction = connection.BeginTransaction(deferred: true); work.RequireCurrent(connection, transaction);
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
        using var transaction = connection.BeginTransaction(deferred: true); work.RequireCurrent(connection, transaction);
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
