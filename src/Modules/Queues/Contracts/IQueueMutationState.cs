using NexusPipeline.Modules.Queues;

namespace NexusPipeline.Modules.Queues.Contracts;

/// <summary>Typed mutation boundary for queue definitions.</summary>
internal interface IQueueMutationState
{
    DispatchQueue? Find(string id);

    IReadOnlyList<DispatchQueue> Snapshot();

    void Mutate(Action<IList<DispatchQueue>> mutation);

    TResult Mutate<TResult>(Func<IList<DispatchQueue>, TResult> mutation);
}
