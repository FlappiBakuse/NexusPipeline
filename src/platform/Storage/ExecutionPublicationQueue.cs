namespace NexusPipeline.Platform.Storage;

internal sealed class ExecutionPublicationQueue
{
    private sealed record Work(Action<ExecutionContext> Run);
    private readonly object _gate = new();
    private readonly Queue<Work> _pending = new();
    private int _count;
    private int _executingThread;
    private readonly AutoResetEvent _available = new(false);
    private Thread? _worker;
    private bool _closed;
    private Action? _release;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int PendingCount { get { lock (_gate) return _count; } }
    internal Task Released => _released.Task;

    internal void RequireNotReentrant()
    {
        if (Volatile.Read(ref _executingThread) == Environment.CurrentManagedThreadId)
            throw new IOException("execution_publication_reentrancy");
    }

    internal T Run<T>(Func<T> callback)
    {
        RequireNotReentrant();
        var context = ExecutionContext.Capture();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_count == 64) throw new IOException("execution_publication_capacity_exceeded");
            _count++;
            _pending.Enqueue(new(neutral =>
            {
                try
                {
                    T result = default!;
                    ExecutionContext.Run(context ?? neutral, _ => result = callback(), null);
                    completion.SetResult(result);
                }
                catch (Exception error) { completion.SetException(error); }
            }));
            if (_worker is null)
            {
                // Publishers synchronously wait for this owner, so its worker must not wait for thread-pool injection.
                _worker = new Thread(Drain) { IsBackground = true, Name = "NexusPublication" };
                try
                {
                    if (ExecutionContext.IsFlowSuppressed()) _worker.Start();
                    else { using (ExecutionContext.SuppressFlow()) _worker.Start(); }
                }
                catch { _worker = null; _pending.Dequeue(); _count--; throw; }
            }
            _available.Set();
        }
        return completion.Task.GetAwaiter().GetResult();
    }

    internal void Close(Action release)
    {
        Action? immediate = null;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            if (_worker is null) { immediate = release; _available.Dispose(); }
            else
            {
                if (_count == 0) immediate = release; else _release = release;
                _available.Set();
            }
        }
        if (immediate is not null) Release(immediate);
    }

    private void Drain()
    {
        var neutral = ExecutionContext.Capture() ?? throw new InvalidOperationException("publication_context_unavailable");
        while (true)
        {
            Work? work; Action? release = null; var ending = false;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out work) && _closed)
                {
                    ending = true; release = _release; _release = null; _available.Dispose();
                }
            }
            if (work is null)
            {
                if (ending) { if (release is not null) Release(release); return; }
                _available.WaitOne(); continue;
            }
            Volatile.Write(ref _executingThread, Environment.CurrentManagedThreadId);
            try { work.Run(neutral); }
            finally
            {
                Volatile.Write(ref _executingThread, 0);
                lock (_gate) _count--;
            }
        }
    }

    private void Release(Action release)
    {
        try { release(); _released.SetResult(); }
        catch (Exception error) { _released.SetException(error); }
    }
}
