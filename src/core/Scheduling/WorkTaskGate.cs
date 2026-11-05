namespace NexusPipeline.Core.Scheduling;

public sealed class WorkTaskGate
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, State> _tasks = [];
    private int _holds;
    private int _uses;

    public WorkTaskUse Enter(Guid taskId) => EnterCore(taskId, null);
    public WorkTaskUse EnterExclusive(Guid taskId, Guid runId)
    {
        if (runId == Guid.Empty) throw new ArgumentException("work_run_identity_required");
        return EnterCore(taskId, runId);
    }

    private WorkTaskUse EnterCore(Guid taskId, Guid? runId)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("work_task_identity_required");
        lock (_gate)
        {
            if (_uses >= 4096) throw new InvalidOperationException("work_task_capacity");
            var state = Get(taskId);
            if (state.Holds != 0) throw new InvalidOperationException("work_task_quiescing");
            if (runId is not null && state.Exclusive is not null) throw new InvalidOperationException("work_task_running");
            if (state.Uses.Count == 0) state.Idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var use = new WorkTaskUse(this, taskId, runId);
            state.Uses.Add(use); _uses++;
            if (runId is not null) state.Exclusive = use;
            return use;
        }
    }

    public void RequireExclusive(Guid taskId, Guid runId)
    {
        lock (_gate)
        {
            if (runId == Guid.Empty || !_tasks.TryGetValue(taskId, out var state) || state.Exclusive is not { } use ||
                use.RunId != runId || use.Cancellation.IsCancellationRequested) throw new UnauthorizedAccessException("work_task_execution_unbound");
        }
    }

    public async ValueTask<IDisposable> QuiesceAsync(IReadOnlyList<Guid> taskIds, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(taskIds); cancellation.ThrowIfCancellationRequested();
        var ids = taskIds.ToArray();
        if (ids.Length is < 1 or > 4097 || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("work_task_selection_invalid");
        WorkTaskUse[] running; Task drained;
        lock (_gate)
        {
            if (_holds >= 32 || _tasks.Count + ids.Count(id => !_tasks.ContainsKey(id)) > 8192)
                throw new InvalidOperationException("work_task_capacity");
            var states = ids.Select(Get).ToArray();
            foreach (var state in states) state.Holds++;
            _holds++;
            running = states.SelectMany(state => state.Uses).ToArray();
            drained = Task.WhenAll(states.Where(state => state.Uses.Count != 0).Select(state => state.Idle!.Task));
        }
        var hold = new Hold(this, ids);
        List<Exception>? failures = null;
        foreach (var use in running)
            try { use.Cancel(); } catch (Exception error) { (failures ??= []).Add(error); }
        try
        {
            await drained.WaitAsync(cancellation).ConfigureAwait(false);
            if (failures is not null) throw new AggregateException(failures);
            cancellation.ThrowIfCancellationRequested();
            return hold;
        }
        catch
        {
            // A canceled waiter cannot reopen entry while a callback still owns the task.
            if (drained.IsCompleted) hold.Dispose();
            else _ = ReleaseAsync(drained, hold);
            throw;
        }
    }

    private static async Task ReleaseAsync(Task drained, Hold hold)
    {
        try { await drained.ConfigureAwait(false); }
        finally { hold.Dispose(); }
    }

    private State Get(Guid id)
    {
        if (_tasks.TryGetValue(id, out var state)) return state;
        if (_tasks.Count >= 8192) throw new InvalidOperationException("work_task_capacity");
        _tasks.Add(id, state = new()); return state;
    }

    internal void Exit(Guid id, WorkTaskUse use)
    {
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var state) || !state.Uses.Remove(use)) throw new InvalidOperationException("work_task_use_unbound");
            _uses--;
            if (ReferenceEquals(state.Exclusive, use)) state.Exclusive = null;
            if (state.Uses.Count == 0) idle = state.Idle;
            if (state.Holds == 0 && state.Uses.Count == 0) _tasks.Remove(id);
        }
        idle?.TrySetResult();
    }

    private void Release(Guid[] ids)
    {
        lock (_gate)
        {
            foreach (var id in ids)
            {
                var state = _tasks[id];
                if (state.Holds <= 0) throw new InvalidOperationException("work_task_hold_unbound");
                state.Holds--;
                if (state.Holds == 0 && state.Uses.Count == 0) _tasks.Remove(id);
            }
            _holds--;
        }
    }

    private sealed class State
    {
        internal int Holds;
        internal WorkTaskUse? Exclusive;
        internal HashSet<WorkTaskUse> Uses { get; } = [];
        internal TaskCompletionSource? Idle;
    }

    private sealed class Hold(WorkTaskGate owner, Guid[] ids) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(ids); }
    }
}

public sealed class WorkTaskUse : IDisposable
{
    private readonly WorkTaskGate _owner;
    private readonly Guid _taskId;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private bool _ended;
    private int _canceling;
    public CancellationToken Cancellation { get; }
    internal Guid? RunId { get; }
    internal WorkTaskUse(WorkTaskGate owner, Guid taskId, Guid? runId)
    { _owner = owner; _taskId = taskId; RunId = runId; Cancellation = _cancellation.Token; }

    internal void Cancel()
    {
        lock (_gate) { if (_ended) return; _canceling++; }
        try { _cancellation.Cancel(); }
        finally
        {
            var exit = false;
            lock (_gate)
            {
                if (--_canceling == 0 && _ended) { _cancellation.Dispose(); exit = true; }
            }
            if (exit) _owner.Exit(_taskId, this);
        }
    }

    public void Dispose()
    {
        var exit = false;
        lock (_gate)
        {
            if (_ended) return;
            _ended = true;
            if (_canceling == 0) { _cancellation.Dispose(); exit = true; }
        }
        if (exit) _owner.Exit(_taskId, this);
    }
}
