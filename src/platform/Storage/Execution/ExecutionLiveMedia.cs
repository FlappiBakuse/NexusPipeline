using System.Runtime.Versioning;
using NexusPipeline.Core.Media;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
public sealed class ExecutionLiveMedia : IRunMediaAuthorization, IMediaFrameSource
{
    private readonly object _gate = new();
    private readonly Guid _instanceId;
    private readonly Dictionary<RunMediaContext, Step> _steps = [];
    private readonly Dictionary<Guid, Grant> _grants = [];
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
        lock (_gate)
        {
            if (!_steps.TryGetValue(context, out var step) || step.Closing || step.Subject != serverSubject || _grants.Count >= 128)
                throw Denied();
            step.RequireLive();
            var grant = new Grant(step, extensionId);
            // Core performs authorization during Open; a provisional grant exists only while this lock is held.
            step.Opening = grant;
            try
            {
                var preview = Previews.Open(context, extensionId, providerId);
                grant.Preview = preview;
                _grants.Add(preview.PreviewId, grant);
                return new(this, grant);
            }
            finally { step.Opening = null; }
        }
    }

    public void AssertAllowed(RunMediaContext context, string extensionId)
    {
        lock (_gate)
        {
            if (!_steps.TryGetValue(context, out var step) || step.Closing ||
                !(step.Opening?.ExtensionId == extensionId || _grants.Values.Any(grant =>
                    ReferenceEquals(grant.Step, step) && grant.ExtensionId == extensionId && !grant.Closed))) throw Denied();
            step.RequireLive();
        }
    }

    public ValueTask<PreviewImage> CaptureAsync(MediaCaptureRequest request, CancellationToken cancellationToken)
    {
        Grant grant;
        lock (_gate)
        {
            if (!_grants.TryGetValue(request.PreviewId, out grant!) || grant.Step.Context != request.Context || grant.ExtensionId != request.ExtensionId ||
                request.RequestId == Guid.Empty || request.Sequence <= grant.Sequence || request.Sequence > 9007199254740991 ||
                grant.SourceBusy) throw Denied();
            Require(grant); grant.Sequence = request.Sequence; grant.SourceBusy = true;
        }
        return CaptureSourceAsync(grant, cancellationToken);
    }

    private async ValueTask<PreviewImage> CaptureSourceAsync(Grant grant, CancellationToken cancellation)
    {
        var task = Enter(grant);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, grant.Step.Stopping.Token, grant.Stopping.Token);
            var image = await grant.Step.Capture(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate) Require(grant);
            return image;
        }
        finally { lock (_gate) grant.SourceBusy = false; Leave(grant, task); }
    }

    internal async ValueTask<PreviewImage> CapturePreviewAsync(Grant grant, CancellationToken cancellation)
    {
        var task = Enter(grant);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, grant.Step.Stopping.Token, grant.Stopping.Token);
            var frame = await grant.Preview!.CaptureAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate) Require(grant);
            return frame;
        }
        finally { Leave(grant, task); }
    }

    private TaskCompletionSource Enter(Grant grant)
    {
        lock (_gate)
        {
            Require(grant);
            var task = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            grant.Step.Active.Add(task); grant.Active.Add(task); return task;
        }
    }

    private void Leave(Grant grant, TaskCompletionSource task)
    { lock (_gate) { grant.Step.Active.Remove(task); grant.Active.Remove(task); task.TrySetResult(); } }

    private void Require(Grant grant)
    {
        if (grant.Closed || grant.Step.Closing || !_steps.TryGetValue(grant.Step.Context, out var step) ||
            !ReferenceEquals(step, grant.Step) || grant.Preview is null ||
            !_grants.TryGetValue(grant.Preview.PreviewId, out var current) || !ReferenceEquals(current, grant)) throw Denied();
        step.RequireLive();
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
        internal Grant? Opening;
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
