using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Platform.Extensions;

public sealed class ExtensionScope : IExtensionScope
{
    private readonly object _gate = new();
    private readonly List<IAsyncDisposable> _resources = [];
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private Task? _disposeTask;
    private int _uses;
    private int _cleanupFailed;
    internal bool HasCleanupFailure => Volatile.Read(ref _cleanupFailed) != 0;
    private TaskCompletionSource? _idle;
    internal ExtensionActivation Activation { get; }
    internal bool CleanupCompletedSuccessfully => Volatile.Read(ref _disposeTask)?.IsCompletedSuccessfully == true;
    private readonly Action<ExtensionScope>? _stopped;

    public ExtensionScope(string extensionId) : this(extensionId, null) { }

    internal ExtensionScope(string extensionId, Action<ExtensionScope>? stopped)
    {
        _stopped = stopped;
        Activation = new(extensionId);
        _token = _cancellation.Token;
    }

    public CancellationToken Cancellation => _token;
    public bool IsPublished => Activation.IsPublished;
    public Guid ActivationId => Activation.ActivationId;
    internal void Publish() => Activation.Publish();

    internal IDisposable BeginUse()
    {
        lock (_gate)
        {
            if (_disposeTask is not null || !Activation.IsAccepting || !Activation.IsPublished)
                throw new IOException("extension_activation_not_published");
            return BeginUseLocked();
        }
    }

    internal IDisposable BeginActivationUse()
    {
        lock (_gate)
        {
            if (_disposeTask is not null || !Activation.IsAccepting || Activation.IsPublished)
                throw new IOException("extension_activation_not_pending");
            return BeginUseLocked();
        }
    }

    private IDisposable BeginUseLocked()
    {
        if (_uses == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _uses = checked(_uses + 1);
        return new Use(this);
    }

    private sealed class Use(ExtensionScope owner) : IDisposable
    {
        private ExtensionScope? _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._gate)
            {
                owner._uses--;
                if (owner._uses == 0) owner._idle!.TrySetResult();
            }
        }
    }

    public async ValueTask OwnAsync(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _resources.Add(resource);
                return;
            }
        }
        await resource.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        IAsyncDisposable[] resources;
        Task inflight;
        lock (_gate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Activation.Revoke();
            _disposeTask = completion.Task;
            resources = _resources.ToArray();
            _resources.Clear();
            inflight = _idle?.Task ?? Task.CompletedTask;
        }
        _ = DisposeResourcesAsync(resources, inflight, completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeResourcesAsync(IAsyncDisposable[] resources, Task inflight, TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try { _cancellation.Cancel(); }
        catch (Exception exception) { Volatile.Write(ref _cleanupFailed, 1); failures.Add(exception); }
        await inflight.ConfigureAwait(false);
        foreach (var resource in resources.Reverse())
        {
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { Volatile.Write(ref _cleanupFailed, 1); failures.Add(exception); }
        }
        _cancellation.Dispose();
        if (failures.Count == 0)
        {
            try { _stopped?.Invoke(this); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0)
            completion.TrySetException(new AggregateException("Extension cleanup failed", failures));
        else
            completion.TrySetResult();
    }
}
