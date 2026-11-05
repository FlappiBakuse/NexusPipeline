using System.Collections.Concurrent;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AdmissionCommitQueue
{
    private sealed record Work(Func<IExecutionAdmissionReceipt> Commit, TaskCompletionSource<IExecutionAdmissionReceipt> Completion);
    private readonly ConcurrentQueue<Work> _pending = new();
    private int _count;
    private int _draining;
    internal int Count => Volatile.Read(ref _count);

    internal IExecutionAdmissionReceipt Run(Func<IExecutionAdmissionReceipt> commit)
    {
        if (Interlocked.Increment(ref _count) > 64)
        {
            Interlocked.Decrement(ref _count);
            throw new ExecutionSelectionException("execution_admission_queue_full");
        }
        var completion = new TaskCompletionSource<IExecutionAdmissionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Enqueue(new(commit, completion));
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0) _ = Task.Run(Drain);
        return completion.Task.GetAwaiter().GetResult();
    }

    private void Drain()
    {
        while (true)
        {
            while (_pending.TryDequeue(out var work))
            {
                try { work.Completion.SetResult(work.Commit()); }
                catch (Exception error) { work.Completion.SetException(error); }
                finally { Interlocked.Decrement(ref _count); }
            }
            Volatile.Write(ref _draining, 0);
            if (_pending.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        }
    }
}
