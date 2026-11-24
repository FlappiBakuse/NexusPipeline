using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using System.Collections.Concurrent;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class ExecutionAdmissionInbox
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AcceptedExecutionAdmission> _pending = [];
    private readonly HashSet<Guid> _reserved = [];
    private readonly SemaphoreSlim _available = new(0, 64);
    private readonly int _capacity;
    private ResourceLeaseManager? _resources;
    private RecoveryIndex? _index;
    private Guid _instanceId;
    private string? _runtimeRoot;
    internal AdmissionCommitQueue Commits { get; } = new();
    public int PendingCount { get { lock (_gate) return _pending.Count; } }
    internal int Capacity => _capacity;

    public ExecutionAdmissionInbox() : this(64) { }

    internal ExecutionAdmissionInbox(int capacity)
    {
        if (capacity is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal IDisposable Reserve(Guid operationId)
    {
        lock (_gate)
        {
            if (_pending.ContainsKey(operationId) || _reserved.Contains(operationId))
                throw new IOException("execution_admission_already_owned");
            if (_pending.Count + _reserved.Count >= _capacity)
                throw new ExecutionSelectionException("execution_admission_inbox_full");
            _reserved.Add(operationId);
            return new Reservation(this, operationId);
        }
    }

    internal void Bind(RuntimeInstance instance, ResourceLeaseManager resources, RecoveryIndex index)
    {
        lock (_gate)
        {
            if (_resources is not null && (!ReferenceEquals(_resources, resources) || !ReferenceEquals(_index, index) ||
                _instanceId != instance.InstanceId || !StringComparer.OrdinalIgnoreCase.Equals(_runtimeRoot, instance.RuntimeRoot)))
                throw new IOException("execution_admission_inbox_unbound");
            _resources = resources; _index = index;
            _instanceId = instance.InstanceId; _runtimeRoot = instance.RuntimeRoot;
        }
    }

    internal void Publish(OperationIdentity identity, PreparedExecutionAdmission prepared, ResourceOperationLease root,
        AttemptAcceptanceResult receipt)
    {
        prepared.RequireLive();
        if (prepared.Identity != identity || receipt.Acceptance.OperationId != identity.OperationId || !receipt.Acceptance.IsNew ||
            prepared.Attempt.Plan.ReadWire().JournalId != receipt.JournalId.ToString("D") || prepared.Attempt.Reference != receipt.Plan)
            throw new IOException("execution_admission_handoff_unbound");
        var accepted = new AcceptedExecutionAdmission(identity, prepared, root, receipt);
        lock (_gate)
        {
            if (!_reserved.Contains(identity.OperationId) || !_pending.TryAdd(identity.OperationId, accepted))
                throw new IOException("execution_admission_handoff_unreserved");
            _reserved.Remove(identity.OperationId);
            _available.Release();
        }
    }

    internal AcceptedExecutionAdmission? Take(Guid operationId)
    {
        lock (_gate)
        {
            if (!_pending.Remove(operationId, out var accepted)) return null;
            _ = _available.Wait(0); return accepted;
        }
    }

    internal AcceptedExecutionAdmission? TakeNext()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return null;
            var next = _pending.First(); _pending.Remove(next.Key); _ = _available.Wait(0); return next.Value;
        }
    }

    internal async Task<AcceptedExecutionAdmission> WaitNextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_pending.Count == 0) continue;
                var next = _pending.First(); _pending.Remove(next.Key); return next.Value;
            }
        }
    }

    private sealed class Reservation(ExecutionAdmissionInbox owner, Guid operationId) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate) owner._reserved.Remove(operationId);
        }
    }
}

internal sealed class AcceptedExecutionAdmission(OperationIdentity identity, PreparedExecutionAdmission prepared,
    ResourceOperationLease root, AttemptAcceptanceResult receipt) : IDisposable
{
    internal OperationIdentity Identity { get; } = identity;
    internal PreparedExecutionAdmission Prepared { get; } = prepared;
    internal ResourceOperationLease Root { get; } = root;
    internal AttemptAcceptanceResult Receipt { get; } = receipt;
    public void Dispose() { Prepared.Dispose(); Root.Dispose(); }
}

internal sealed class AdmissionCommitQueue
{
    private sealed record Work(Guid OperationId, Func<IExecutionAdmissionReceipt> Commit, TaskCompletionSource<IExecutionAdmissionReceipt> Completion);
    private readonly ConcurrentQueue<Work> _pending = new();
    private int _count;
    private int _draining;
    internal bool IsWaiting(Guid operationId) => _pending.Any(item => item.OperationId == operationId);

    internal IExecutionAdmissionReceipt Run(Guid operationId, Func<IExecutionAdmissionReceipt> commit)
    {
        if (Interlocked.Increment(ref _count) > 64)
        {
            Interlocked.Decrement(ref _count);
            throw new ExecutionSelectionException("execution_admission_queue_full");
        }
        var completion = new TaskCompletionSource<IExecutionAdmissionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Enqueue(new(operationId, commit, completion));
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
