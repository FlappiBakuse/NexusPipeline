using System.Runtime.Versioning;
using NexusPipeline.Core.Media;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
public sealed class ExecutionLiveMedia : IRunMediaAuthorization, IMediaFrameSource
{
    [ThreadStatic] private static Grant? _opening;
    private readonly object _gate = new();
    private readonly Guid _instanceId;
    private readonly Dictionary<RunMediaContext, Step> _steps = [];
    private readonly Dictionary<Guid, Grant> _grants = [];
    private readonly HashSet<Grant> _pendingOpens = [];
    public MediaPreviewRegistry Previews { get; }

    public ExecutionLiveMedia(Guid instanceId)
    {
        if (instanceId == Guid.Empty) throw new ArgumentException("media_instance_required");
        _instanceId = instanceId; Previews = new(this);
    }

    internal Step Register(string subject, RunMediaContext context, Action requireLive,
        Func<CancellationToken, ValueTask<PreviewImage>> capture, string? source = null)
    {
        if (context.InstanceId != _instanceId || string.IsNullOrWhiteSpace(subject)) throw Denied();
        ArgumentNullException.ThrowIfNull(requireLive);
        ArgumentNullException.ThrowIfNull(capture);
        lock (_gate)
        {
            if (source is not (null or "pc_game" or "emulator")) throw Denied();
            var step = new Step(this, subject, context, requireLive, capture, source);
            if (_steps.Count >= 32 || !_steps.TryAdd(context, step)) throw Denied();
            return step;
        }
    }

    public ExecutionLivePreview OpenPreview(string serverSubject, RunMediaContext context, string extensionId, string providerId)
    {
        Grant grant;
        TaskCompletionSource task;
        lock (_gate)
        {
            if (!_steps.TryGetValue(context, out var step) || step.Closing || step.Subject != serverSubject ||
                _grants.Count + _pendingOpens.Count >= 128)
                throw Denied();
            grant = new Grant(step, extensionId);
            _pendingOpens.Add(grant);
            task = Track(grant);
        }
        var previous = _opening;
        MediaPreview? preview = null;
        try
        {
            grant.Step.RequireLive();
            lock (_gate) RequirePending(grant);
            // Core authorizes synchronously during Open. Only this call can use its pending grant.
            _opening = grant;
            preview = Previews.Open(context, extensionId, providerId);
            lock (_gate)
            {
                RequirePending(grant);
                grant.Preview = preview;
                _grants.Add(preview.PreviewId, grant);
                _pendingOpens.Remove(grant);
            }
            return new(this, grant);
        }
        finally
        {
            _opening = previous;
            bool abandoned;
            lock (_gate) abandoned = _pendingOpens.Remove(grant);
            try
            {
                if (abandoned)
                {
                    try { preview?.Dispose(); }
                    finally { grant.Stopping.Dispose(); }
                }
            }
            finally { Leave(grant, task); }
        }
    }

    public void AssertAllowed(RunMediaContext context, string extensionId)
    {
        Step step;
        TaskCompletionSource task;
        lock (_gate)
        {
            step = RequireAllowedStep(context, extensionId);
            task = Track(step);
        }
        try
        {
            step.RequireLive();
            lock (_gate)
                if (!ReferenceEquals(step, RequireAllowedStep(context, extensionId))) throw Denied();
        }
        finally { lock (_gate) Complete(step, task); }
    }

    private Step RequireAllowedStep(RunMediaContext context, string extensionId)
    {
        if (!_steps.TryGetValue(context, out var step) || step.Closing) throw Denied();
        // Core authorizes the step and extension. A different open grant can still authorize that pair.
        if (_opening is { } pending && ReferenceEquals(pending.Step, step) &&
            pending.ExtensionId == extensionId && _pendingOpens.Contains(pending)) return step;
        if (_grants.Values.Any(grant => ReferenceEquals(grant.Step, step) && grant.ExtensionId == extensionId && !grant.Closed))
            return step;
        throw Denied();
    }

    public ValueTask<PreviewImage> CaptureAsync(MediaCaptureRequest request, CancellationToken cancellationToken)
    {
        Grant grant;
        TaskCompletionSource task;
        lock (_gate)
        {
            if (!_grants.TryGetValue(request.PreviewId, out grant!) || grant.Step.Context != request.Context || grant.ExtensionId != request.ExtensionId ||
                request.RequestId == Guid.Empty || request.Sequence > 9007199254740991) throw Denied();
            RequireCurrent(grant);
            RequireSequence(grant, request.Sequence);
            task = Track(grant);
        }
        try
        {
            grant.Step.RequireLive();
            lock (_gate)
            {
                RequireCurrent(grant);
                RequireSequence(grant, request.Sequence);
                grant.Sequence = request.Sequence;
                grant.SourceBusy = true;
            }
        }
        catch { Leave(grant, task); throw; }
        return CaptureSourceAsync(grant, task, cancellationToken);
    }

    private async ValueTask<PreviewImage> CaptureSourceAsync(Grant grant, TaskCompletionSource task, CancellationToken cancellation)
    {
        try
        {
            RequireLive(grant);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, grant.Step.Stopping.Token, grant.Stopping.Token);
            var image = await grant.Step.Capture(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            RequireLive(grant);
            return image;
        }
        finally { lock (_gate) grant.SourceBusy = false; Leave(grant, task); }
    }

    internal async ValueTask<PreviewImage> CapturePreviewAsync(Grant grant, CancellationToken cancellation)
    {
        var task = Enter(grant);
        try
        {
            RequireLive(grant);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, grant.Step.Stopping.Token, grant.Stopping.Token);
            var frame = await grant.Preview!.CaptureAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            RequireLive(grant);
            return frame;
        }
        finally { Leave(grant, task); }
    }

    private TaskCompletionSource Enter(Grant grant)
    {
        lock (_gate)
        {
            RequireCurrent(grant);
            return Track(grant);
        }
    }

    private static TaskCompletionSource Track(Grant grant)
    {
        var task = Track(grant.Step);
        grant.Active.Add(task);
        return task;
    }

    private static TaskCompletionSource Track(Step step)
    {
        var task = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        step.Active.Add(task);
        return task;
    }

    private void Leave(Grant grant, TaskCompletionSource task)
    {
        lock (_gate)
        {
            grant.Active.Remove(task);
            Complete(grant.Step, task);
        }
    }

    private static void Complete(Step step, TaskCompletionSource task)
    {
        step.Active.Remove(task);
        task.TrySetResult();
    }

    private void RequireLive(Grant grant)
    {
        lock (_gate) RequireCurrent(grant);
        grant.Step.RequireLive();
        lock (_gate) RequireCurrent(grant);
    }

    private void RequirePending(Grant grant)
    {
        RequireStep(grant);
        if (!_pendingOpens.Contains(grant)) throw Denied();
    }

    private void RequireStep(Grant grant)
    {
        if (grant.Closed || grant.Step.Closing || !_steps.TryGetValue(grant.Step.Context, out var step) ||
            !ReferenceEquals(step, grant.Step)) throw Denied();
    }

    private void RequireCurrent(Grant grant)
    {
        RequireStep(grant);
        if (ReferenceEquals(_opening, grant) && _pendingOpens.Contains(grant)) return;
        if (grant.Preview is null ||
            !_grants.TryGetValue(grant.Preview.PreviewId, out var current) || !ReferenceEquals(current, grant)) throw Denied();
    }

    private static void RequireSequence(Grant grant, long sequence)
    {
        if (sequence <= grant.Sequence || grant.SourceBusy) throw Denied();
    }

    internal async ValueTask CloseAsync(Grant grant)
    {
        Close(grant);
        await grant.ClosedSignal.Task.ConfigureAwait(false);
        Task[] pending;
        lock (_gate) pending = grant.Active.Select(task => task.Task).ToArray();
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        ReleaseStopping(grant);
    }

    private void ReleaseStopping(Grant grant)
    { lock (_gate) { if (grant.StoppingDisposed) return; grant.StoppingDisposed = true; grant.Stopping.Dispose(); } }

    private void Close(Grant grant)
    {
        lock (_gate)
        {
            if (grant.Closed) return;
            grant.Closed = true;
            if (grant.Preview is { } preview) _grants.Remove(preview.PreviewId);
        }
        try { grant.Stopping.Cancel(); }
        finally
        {
            try { grant.Preview?.Dispose(); }
            finally { grant.ClosedSignal.TrySetResult(); }
        }
    }

    private async Task CloseStepAsync(Step step, TimeSpan timeout)
    {
        Grant[] grants;
        lock (_gate)
        {
            if (step.Closed) return;
            step.Closing = true;
            grants = _grants.Values.Where(grant => ReferenceEquals(grant.Step, step)).ToArray();
        }
        step.Stopping.Cancel();
        foreach (var grant in grants) Close(grant);
        await Task.WhenAll(grants.Select(grant => grant.ClosedSignal.Task)).ConfigureAwait(false);
        Task[] active;
        lock (_gate) active = step.Active.Select(task => task.Task).ToArray();
        await Task.WhenAll(active).WaitAsync(timeout).ConfigureAwait(false);
        lock (_gate)
        {
            if (step.Active.Count != 0) throw new IOException("media_step_collection_pending");
            if (_steps.TryGetValue(step.Context, out var current) && ReferenceEquals(step, current)) _steps.Remove(step.Context);
            step.Closed = true;
        }
        foreach (var grant in grants) ReleaseStopping(grant);
        step.Stopping.Dispose();
    }

    internal sealed class Step(ExecutionLiveMedia owner, string subject, RunMediaContext context,
        Action requireLive, Func<CancellationToken, ValueTask<PreviewImage>> capture, string? source) : IAsyncDisposable
    {
        internal string Subject { get; } = subject;
        internal string? Source { get; } = source;
        internal RunMediaContext Context { get; } = context;
        internal Action RequireLive { get; } = requireLive;
        internal Func<CancellationToken, ValueTask<PreviewImage>> Capture { get; } = capture;
        internal readonly HashSet<TaskCompletionSource> Active = [];
        internal readonly CancellationTokenSource Stopping = new();
        internal bool Closing, Closed;
        internal Task CloseAsync(TimeSpan timeout) => owner.CloseStepAsync(this, timeout);
        public ValueTask DisposeAsync() => new(CloseAsync(TimeSpan.FromSeconds(15)));
    }

    internal sealed class Grant(Step step, string extensionId)
    {
        internal readonly Step Step = step;
        internal readonly HashSet<TaskCompletionSource> Active = [];
        internal readonly TaskCompletionSource ClosedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool StoppingDisposed;
        internal readonly string ExtensionId = extensionId;
        internal readonly CancellationTokenSource Stopping = new();
        internal MediaPreview? Preview;
        internal long Sequence;
        internal bool SourceBusy, Closed;
    }

    private static UnauthorizedAccessException Denied() => new("run_media_grant_required");
}

[SupportedOSPlatform("windows")]
public sealed class ExecutionLivePreview : IAsyncDisposable
{
    private readonly ExecutionLiveMedia _owner;
    private readonly ExecutionLiveMedia.Grant _grant;
    internal ExecutionLivePreview(ExecutionLiveMedia owner, ExecutionLiveMedia.Grant grant) { _owner = owner; _grant = grant; }
    public Guid PreviewId => _grant.Preview!.PreviewId;
    public string? Source => _grant.Step.Source;
    public ValueTask<PreviewImage> CaptureAsync(CancellationToken cancellationToken = default) => _owner.CapturePreviewAsync(_grant, cancellationToken);
    public ValueTask DisposeAsync() => _owner.CloseAsync(_grant);
}
