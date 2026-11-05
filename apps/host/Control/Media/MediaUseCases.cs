using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Media;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Host.Control.Media;

[SupportedOSPlatform("windows")]
internal sealed class MediaUseCases(Guid instanceId, ExecutionLiveMedia media) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _handles = [];
    private bool _stopping;

    internal async Task<object> OpenAsync(AuthenticatedControlRequest request, Guid runId, Guid attemptId, string extensionId, string providerId)
    {
        request.Require(ControlCapability.MediaGrant);
        lock (_gate) if (_stopping || _handles.Count >= 128) throw new ControlRequestException(409, "media_grant_limit");
        var preview = media.OpenPreview(request.SubjectId, new(instanceId, runId, attemptId), extensionId, providerId);
        try
        {
            request.Require(ControlCapability.MediaGrant);
            lock (_gate)
            {
                if (_stopping || _handles.Count >= 128) throw new ControlRequestException(409, "media_grant_limit");
                _handles.Add(preview.PreviewId, new(request.SubjectId, preview));
            }
            return new { preview.PreviewId, runId, attemptId, preview.Source };
        }
        catch { await preview.DisposeAsync(); throw; }
    }

    internal async ValueTask<PreviewImage> CaptureAsync(AuthenticatedControlRequest request, Guid id, CancellationToken cancellation)
    {
        request.Require(ControlCapability.MediaGrant);
        Entry entry;
        lock (_gate)
        {
            entry = Require(request.SubjectId, id);
            if (_stopping || entry.Closing) throw new ControlRequestException(409, "media_grant_closed");
        }
        PreviewImage image;
        try { image = await entry.Preview.CaptureAsync(cancellation); }
        catch (PreviewUnavailableException)
        {
            request.Require(ControlCapability.MediaGrant);
            lock (_gate)
                if (_stopping || entry.Closing || !_handles.TryGetValue(id, out var pending) || !ReferenceEquals(entry, pending))
                    throw new ControlRequestException(409, "media_grant_closed");
            throw;
        }
        request.Require(ControlCapability.MediaGrant);
        lock (_gate)
            if (_stopping || entry.Closing || !_handles.TryGetValue(id, out var current) || !ReferenceEquals(entry, current))
                throw new ControlRequestException(409, "media_grant_closed");
        return image;
    }

    internal async Task CloseAsync(AuthenticatedControlRequest request, Guid id)
    {
        request.Require(ControlCapability.MediaGrant);
        Entry entry;
        lock (_gate) { entry = Require(request.SubjectId, id); entry.Closing = true; }
        await entry.Preview.DisposeAsync();
        lock (_gate) _handles.Remove(id);
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] handles;
        lock (_gate)
        {
            _stopping = true;
            handles = _handles.Values.ToArray();
            foreach (var item in handles) item.Closing = true;
        }
        foreach (var entry in handles)
        {
            await entry.Preview.DisposeAsync();
            lock (_gate) _handles.Remove(entry.Preview.PreviewId);
        }
    }
    private Entry Require(string subject, Guid id) => _handles.TryGetValue(id, out var entry) && entry.Subject == subject
        ? entry : throw new ControlRequestException(404, "media_grant_not_found");
    private sealed class Entry(string subject, ExecutionLivePreview preview)
    {
        internal string Subject { get; } = subject;
        internal ExecutionLivePreview Preview { get; } = preview;
        internal bool Closing { get; set; }
    }
}
