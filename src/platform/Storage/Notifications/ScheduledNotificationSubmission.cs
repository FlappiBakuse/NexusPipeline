using System.Runtime.Versioning;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class ScheduledNotificationSubmission(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ScheduledProviderDispatch dispatch, NexusPipeline.Core.Maintenance.ResourceLeaseManager resources,
    ExtensionScope actualScope, IWorkQueueAcceptance queue)
    : INotificationSubmissionAuthorization, INotificationSubmissionQueue
{
    public void AssertAllowed(NotificationSubmission submission)
    {
        var work = Require(submission);
        lease.PublishExecution(() =>
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            dispatch.RequireSubmissionInside(work, actualScope, connection, transaction);
        });
    }

    public ValueTask<NotificationQueueAcceptance> AcceptAsync(NotificationSubmission submission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var work = Require(submission);
        using var root = dispatch.AcquireSubmissionRoot(work);
        using var held = root.Borrow(resources, submission.OperationId, AcceptedProviderInvocation.Claims(work.Entry.ExtensionId));
        return new(held.Use(() =>
        {
            lease.VerifyHeld(instance); cancellationToken.ThrowIfCancellationRequested();
            var prior = lease.PublishExecution(() => ReadExisting(submission, work));
            if (prior is not null) return prior;
            using var prepared = PreparedScheduledSource.Capture(instance, database, contracts, work, submission);
            using var activity = queue.Prepare(work.Activity);
            return lease.PublishExecution(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var replay = ReadExisting(submission, work);
                if (replay is not null) return replay;
                database.Write((connection, transaction) =>
                {
                    dispatch.RequireSubmissionInside(work, actualScope, connection, transaction);
                    prepared.AcceptInside(connection, transaction, work.Source.Document.Invocation.OccurrenceId, DateTimeOffset.UtcNow, lease.StartupGeneration);
                    return true;
                });
                activity.Committed(prepared.Source.Reference);
                return new NotificationQueueAcceptance(submission.OperationId, prepared.Source.Document.OperationId, true);
            });
        }));
    }

    private AcceptedProviderInvocation Require(NotificationSubmission submission)
    {
        lease.VerifyHeld(instance);
        if (submission.ExtensionId != actualScope.Activation.ExtensionId) throw new UnauthorizedAccessException("scheduled_invocation_not_active");
        return dispatch.RequireActual(submission.Invocation, submission.ExtensionId, actualScope: actualScope);
    }

    private NotificationQueueAcceptance? ReadExisting(NotificationSubmission submission, AcceptedProviderInvocation work)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        dispatch.RequireSubmissionInside(work, actualScope, connection, transaction);
        var reference = NotificationScheduledRows.Lookup(connection, transaction, submission.OperationId);
        if (reference is null)
        {
            using var occupied = NotificationDeliveryRows.Command(connection, transaction,
                "SELECT 1 FROM operations WHERE operation_id=$0;", submission.OperationId.ToString("D"));
            if (occupied.ExecuteScalar() is not null) throw NotificationScheduledCodec.Invalid();
            return null;
        }
        var source = new NotificationScheduledCodec(instance, contracts).Read(reference, connection, transaction);
        if (source.Document.Invocation != work.Source.Reference || source.Document.Title != submission.Title ||
            source.Document.Body != submission.Body || !source.Document.Targets.Select(item => item.TaskId).SequenceEqual(submission.NotificationTaskIds))
            throw NotificationScheduledCodec.Invalid();
        transaction.Commit();
        return new(submission.OperationId, source.Document.OperationId, false);
    }
}
