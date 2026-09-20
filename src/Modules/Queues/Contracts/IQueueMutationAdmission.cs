namespace NexusPipeline.Modules.Queues.Contracts;

internal sealed record QueueMutationAdmissionResult(
    bool Allowed,
    IReadOnlyList<string> RunIds,
    string? FailureCode);

/// <summary>Queue-facing execution lease/admission port.</summary>
internal interface IQueueMutationAdmission
{
    QueueMutationAdmissionResult TryExecute(string queueId, Action mutation);

    QueueMutationAdmissionResult TryExecuteAny(Action mutation);
}
