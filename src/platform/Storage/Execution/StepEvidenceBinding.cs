namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record StepEvidenceBinding(Guid InstanceId, Guid ParentJournalId, Guid ParentOperationId,
    Guid RunId, Guid AttemptId, string StepId, string JobName, Guid WriterId, int SessionId, string PlanDigest);
