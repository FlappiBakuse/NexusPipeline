using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed record ExecutionControlAcceptance(OperationAcceptance Acceptance, Guid RunId, Guid AttemptId,
    string Acknowledgement);

[SupportedOSPlatform("windows")]
public sealed class ExecutionControlService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ExecutionAdmissionService admission, ExecutionRetryService retries, ExecutionRuntimeConsumer? runtime = null)
{
    public ExecutionControlAcceptance Start(Guid operationId, string subject, ExecutionSelectionRequest request, DateTimeOffset now)
    {
        RequireInstance();
        var identity = ExecutionStartIdentity.Create(operationId, subject, request);
        var replay = lease.PublishExecution(() => new RunAttemptAcceptance(instance, contracts, database).LookupStart(identity));
        if (replay is not null) return DescribeAccepted(replay, subject);
        if (request.Target is QueueTarget queue)
        {
            var catalog = new NexusPipeline.Platform.Storage.Automation.AutomationDefinitionStore(database).Read();
            if (catalog.Revision == request.ExpectedCatalogRevision &&
                catalog.Queues.SingleOrDefault(item => item.Id == queue.QueueId)?.CompletionAction is { } action &&
                action != NexusPipeline.Core.Automation.AutomationCompletionAction.None)
                throw new UnauthorizedAccessException("completion_local_start_required");
        }
        var accepted = admission.Start(operationId, subject, request, now);
        return DescribeAccepted(accepted, subject);
    }

    public ExecutionControlAcceptance DescribeAccepted(AttemptAcceptanceResult accepted, string subject) =>
        lease.PublishExecution(() =>
        {
            RequireInstance();
            var source = ReadSource(accepted.Acceptance.OperationId, subject);
            var journal = new RecoveryJournalRegistry(database).ReadJournal(source.JournalId);
            if (accepted.JournalId != source.JournalId || accepted.Plan != AttemptPreparedBinding.Reference(instance, journal))
                throw new OperationConflictException();
            var plan = new AttemptPlanCodec(instance, contracts, new(instance, contracts)).ReadMetadata(accepted.Plan).Plan;
            StartSourceRows.RequireAttempt(source, plan);
            return new ExecutionControlAcceptance(accepted.Acceptance, source.RunId,
                Guid.ParseExact(plan.AttemptId, "D"), "accepted");
        });

    public ExecutionControlAcceptance Cancel(Guid operationId, string subject, Guid startOperationId,
        Guid runId, Guid attemptId, DateTimeOffset now)
    {
        var target = lease.PublishExecution(() =>
        {
            RequireInstance();
            var source = ReadSource(startOperationId, subject);
            var journal = new RecoveryJournalRegistry(database).ReadJournal(source.JournalId);
            var plan = AttemptPreparedBinding.ReadPlanMetadata(instance, contracts, journal).Plan;
            StartSourceRows.RequireAttempt(source, plan);
            if (source.RunId != runId || plan.AttemptId != attemptId.ToString("D"))
                throw new OperationConflictException();
            var identity = ExecutionCancellationStore.Identity(operationId, subject, source.JournalId,
                startOperationId, runId, attemptId);
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
            using var transaction = connection.BeginTransaction(deferred: true);
            var existing = OperationRows.ReadOwned(connection, transaction, operationId, subject);
            if (existing is not null && existing.Kind != identity.Kind && existing.Kind != ExecutionRetryCancellation.Kind)
                throw new OperationConflictException();
            var waiting = existing?.Kind == ExecutionRetryCancellation.Kind ||
                existing is null && AttemptReceiptStore.ReadInside(connection, transaction, attemptId) is not null;
            return (source.JournalId, Identity: identity, Waiting: waiting);
        });
        if (target.Waiting)
        {
            var accepted = retries.CancelWaiting(operationId, subject, startOperationId, runId, attemptId, now);
            return new(accepted, runId, attemptId, "retry-declined");
        }
        var result = new ExecutionCancellationStore(instance, database, contracts, lease)
            .Cancel(target.Identity, target.JournalId, now);
        runtime?.ObserveCancellation(startOperationId, runId, attemptId);
        return new(result.Acceptance, runId, attemptId, "dispatch-closed");
    }

    private void RequireInstance()
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("execution_control_instance_unbound");
    }

    private FrozenStartSource ReadSource(Guid operationId, string subject)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        if (OperationRows.ReadOwned(connection, transaction, operationId, subject) is null)
            throw new IOException("execution_control_source_unavailable");
        var source = StartSourceRows.Read(connection, transaction, operationId);
        if (source.Identity.SubjectId != subject) throw new OperationConflictException();
        return source;
    }
}
