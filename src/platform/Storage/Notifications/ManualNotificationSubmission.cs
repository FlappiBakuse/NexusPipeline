using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class ManualNotificationSubmission(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ResourceLeaseManager resources, ExtensionScope scope, PreparedManualCallback callback, IWorkQueueAcceptance queue)
    : INotificationSubmissionAuthorization, INotificationSubmissionQueue
{
    public void AssertAllowed(NotificationSubmission submission)
    {
        _ = Require(submission);
        lease.PublishExecution(() =>
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true); RequireInside(connection, transaction);
        });
    }

    public ValueTask<NotificationQueueAcceptance> AcceptAsync(NotificationSubmission submission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var work = Require(submission);
        using var root = resources.TryContinueRun(work.Activity, submission.OperationId, AcceptedManualWork.Claims(work.Entry.ExtensionId)).Lease ??
            throw new IOException("manual_notification_admission_blocked");
        using var held = root.Borrow(resources, submission.OperationId, AcceptedManualWork.Claims(work.Entry.ExtensionId));
        return new(held.Use(() =>
        {
            lease.VerifyHeld(instance); cancellationToken.ThrowIfCancellationRequested();
            var prior = lease.PublishExecution(() => ReadExisting(submission, work)); if (prior is not null) return prior;
            using var prepared = PreparedManualSource.Capture(instance, database, contracts, work, submission);
            using var activity = queue.Prepare(work.Activity);
            return lease.PublishExecution(() =>
            {
                cancellationToken.ThrowIfCancellationRequested(); var replay = ReadExisting(submission, work); if (replay is not null) return replay;
                database.Write((connection, transaction) =>
                {
                    RequireInside(connection, transaction); cancellationToken.ThrowIfCancellationRequested();
                    prepared.AcceptInside(connection, transaction, work.Source.Document.OperationId, DateTimeOffset.UtcNow, lease.StartupGeneration); return true;
                });
                activity.CommittedManual(prepared.Source.Reference);
                return new NotificationQueueAcceptance(submission.OperationId, prepared.Source.Document.OperationId, true);
            });
        }));
    }

    private AcceptedManualWork Require(NotificationSubmission submission)
    {
        lease.VerifyHeld(instance); callback.RequireCurrent(); var work = callback.Accepted;
        if (!scope.IsPublished || scope.ActivationId != work.Entry.ActivationId || submission.ExtensionId != scope.Activation.ExtensionId ||
            !callback.FrozenInvocation.Matches(submission.Invocation) || submission.OperationId != callback.FrozenInvocation.NotificationOperationId ||
            submission.NotificationTaskIds.Any(id => !callback.FrozenInvocation.NotificationTaskIds.Contains(id)))
            throw new UnauthorizedAccessException("manual_notification_invocation_unbound");
        return work;
    }

    private void RequireInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        callback.RequireCurrent(); var work = callback.Accepted;
        var state = ManualWorkRows.Read(instance, contracts, connection, transaction, work.Source.Reference);
        if (state.Begin is null || state.Result is not null || state.Source.Document.Creator != JournalWriterStore.CurrentWriter ||
            state.Source.Document.OwnerGeneration != lease.StartupGeneration) throw NotificationManualCodec.Invalid();
    }

    private NotificationQueueAcceptance? ReadExisting(NotificationSubmission submission, AcceptedManualWork work)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true); RequireInside(connection, transaction);
        var reference = NotificationManualRows.Lookup(connection, transaction, submission.OperationId);
        if (reference is null)
        {
            using var occupied = NotificationDeliveryRows.Command(connection, transaction,
                "SELECT 1 FROM operations WHERE operation_id=$0;", submission.OperationId.ToString("D"));
            if (occupied.ExecuteScalar() is not null) throw NotificationManualCodec.Invalid(); return null;
        }
        var source = new NotificationManualCodec(instance, contracts).Read(reference, connection, transaction);
        if (source.Document.Invocation != work.Source.Reference || source.Document.Title != submission.Title || source.Document.Body != submission.Body ||
            !source.Document.Targets.Select(item => item.TaskId).SequenceEqual(submission.NotificationTaskIds)) throw NotificationManualCodec.Invalid();
        transaction.Commit(); return new(submission.OperationId, source.Document.OperationId, false);
    }
}
