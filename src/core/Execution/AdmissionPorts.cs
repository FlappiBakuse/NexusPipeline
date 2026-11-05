using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;

namespace NexusPipeline.Core.Execution;

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
