using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;

namespace NexusPipeline.Core.Execution;

public sealed class AdmissionCoordinator(ResourceLeaseManager resources, IExecutionAdmissionPort port)
{
    public IExecutionAdmissionReceipt Start(Guid operationId, string subject, ExecutionSelectionRequest request, DateTimeOffset createdAt)
        => StartCore(operationId, subject, request, createdAt, null);

    public IExecutionAdmissionReceipt ContinueAcceptedRun(AcceptedExecutionRun run, Guid operationId, string subject,
        ExecutionSelectionRequest request, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(run);
        return StartCore(operationId, subject, request, createdAt, run);
    }

    private IExecutionAdmissionReceipt StartCore(Guid operationId, string subject, ExecutionSelectionRequest request,
        DateTimeOffset createdAt, AcceptedExecutionRun? acceptedRun)
    {
        request = ExecutionSelectionPolicy.FreezeRequest(request);
        acceptedRun?.RequireContinuation(operationId, subject, request);
        var identity = port.Identity(operationId, subject, request);
        var replay = port.Lookup(identity);
        if (replay is not null) return replay;
        using var draft = port.Prepare(request, createdAt);
        var admission = acceptedRun is null ? resources.TryAcquire(operationId, draft.Claims) :
            resources.TryContinueRun(acceptedRun, operationId, draft.Claims);
        ResourceOperationLease? root = admission.Lease;
        if (root is null)
            return port.Lookup(identity) ?? throw new ExecutionSelectionException(admission.Denial == ResourceLeaseDenial.Conflict
                ? "execution_resource_conflict" : "execution_host_unavailable");
        IPreparedExecutionAdmission? prepared = null;
        try
        {
            replay = port.Lookup(identity);
            if (replay is not null) return replay;
            prepared = port.Freeze(identity, draft, root);
            var accepted = port.CommitAndHandoff(identity, draft, prepared, root, createdAt);
            if (!accepted.Acceptance.IsNew) return accepted;
            prepared = null; root = null;
            return accepted;
        }
        finally { prepared?.Dispose(); root?.Dispose(); }
    }
}

public interface IExecutionAdmissionReceipt
{
    OperationAcceptance Acceptance { get; }
    Guid JournalId { get; }
}

public interface IExecutionAdmissionDraft : IDisposable
{
    IReadOnlyList<ResourceClaim> Claims { get; }
}

public interface IPreparedExecutionAdmission : IDisposable { }

public interface IExecutionAdmissionPort
{
    OperationIdentity Identity(Guid operationId, string subject, ExecutionSelectionRequest request);
    IExecutionAdmissionReceipt? Lookup(OperationIdentity identity);
    IExecutionAdmissionDraft Prepare(ExecutionSelectionRequest request, DateTimeOffset createdAt);
    IPreparedExecutionAdmission Freeze(OperationIdentity identity, IExecutionAdmissionDraft draft, ResourceOperationLease root);
    IExecutionAdmissionReceipt CommitAndHandoff(OperationIdentity identity, IExecutionAdmissionDraft draft,
        IPreparedExecutionAdmission prepared, ResourceOperationLease root, DateTimeOffset acceptedAt);
}
