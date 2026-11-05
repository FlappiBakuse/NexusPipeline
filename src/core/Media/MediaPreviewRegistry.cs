using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Core.Media;

public sealed class MediaPreviewRegistry(IRunMediaAuthorization authorization)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Extension, string Provider), Registration> _providers = [];
    private readonly Dictionary<Guid, Preview> _previews = [];

    public IDisposable Register(ExtensionActivation activation, string providerId, IMediaFrameSource source)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 80 ||
            providerId.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("media_provider_identity_invalid");
        lock (_gate)
        {
            if (!activation.IsAccepting) throw new InvalidOperationException("extension_activation_revoked");
            if (_providers.Count >= 256) throw new InvalidOperationException("media_provider_limit");
            var registration = new Registration(this, activation, providerId, source);
            if (!_providers.TryAdd((activation.ExtensionId, providerId), registration))
                throw new InvalidOperationException("media_provider_already_registered");
            return registration;
        }
    }

    public MediaPreview Open(RunMediaContext context, string extensionId, string providerId)
    {
        ArgumentNullException.ThrowIfNull(context);
        authorization.AssertAllowed(context, extensionId);
        lock (_gate)
        {
            if (!_providers.TryGetValue((extensionId, providerId), out var registration) || !registration.Activation.IsPublished)
                throw new InvalidOperationException("media_provider_unavailable");
            if (_previews.Count >= 128) throw new InvalidOperationException("media_preview_limit");
            var preview = new Preview(context, registration);
            _previews.Add(preview.Id, preview);
            return new MediaPreview(this, preview.Id);
        }
    }

    internal async ValueTask<PreviewImage> CaptureAsync(Guid previewId, CancellationToken cancellationToken)
    {
        Preview preview;
        MediaCaptureRequest request;
        lock (_gate)
        {
            preview = Current(previewId);
            if (preview.Busy) throw new InvalidOperationException("media_capture_in_progress");
            if (preview.Sequence == 9007199254740991) throw new InvalidOperationException("media_sequence_exhausted");
            preview.Busy = true;
            request = new(preview.Context, preview.Id, Guid.NewGuid(), ++preview.Sequence, preview.Registration.Activation.ExtensionId);
        }
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                preview.Token, preview.Registration.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            authorization.AssertAllowed(preview.Context, preview.Registration.Activation.ExtensionId);
            var frame = await preview.Registration.Source.CaptureAsync(request, cancellation.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("media_frame_missing");
            cancellation.Token.ThrowIfCancellationRequested();
            authorization.AssertAllowed(preview.Context, preview.Registration.Activation.ExtensionId);
            lock (_gate)
            {
                if (!ReferenceEquals(preview, Current(previewId))) throw new InvalidOperationException("media_preview_closed");
                return frame;
            }
        }
        catch (PreviewUnavailableException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            authorization.AssertAllowed(preview.Context, preview.Registration.Activation.ExtensionId);
            lock (_gate) _ = Current(previewId);
            throw;
        }
        finally { lock (_gate) preview.Busy = false; }
    }

    private Preview Current(Guid id)
    {
        if (!_previews.TryGetValue(id, out var preview) || !preview.Registration.Activation.IsPublished ||
            !_providers.TryGetValue((preview.Registration.Activation.ExtensionId, preview.Registration.ProviderId), out var registration) ||
            !ReferenceEquals(registration, preview.Registration))
            throw new InvalidOperationException("media_preview_closed");
        return preview;
    }

    internal void Close(Guid id)
    {
        Preview? removed;
        lock (_gate) _previews.Remove(id, out removed);
        removed?.Cancel();
    }

    private void Remove(Registration registration)
    {
        Preview[] removed;
        lock (_gate)
        {
            var key = (registration.Activation.ExtensionId, registration.ProviderId);
            if (!_providers.TryGetValue(key, out var current) || !ReferenceEquals(registration, current)) return;
            _providers.Remove(key);
            removed = _previews.Values.Where(preview => ReferenceEquals(preview.Registration, registration)).ToArray();
            foreach (var preview in removed) _previews.Remove(preview.Id);
        }
        var failures = new List<Exception>();
        try { registration.Cancellation.Cancel(); }
        catch (Exception exception) { failures.Add(exception); }
        finally { registration.Cancellation.Dispose(); }
        foreach (var preview in removed)
        {
            try { preview.Cancel(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0) throw new AggregateException("media_preview_cleanup_failed", failures);
    }

    private sealed class Registration : IDisposable
    {
        private readonly MediaPreviewRegistry _owner;
        public ExtensionActivation Activation { get; }
        public string ProviderId { get; }
        public IMediaFrameSource Source { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken Token { get; }
        public Registration(MediaPreviewRegistry owner, ExtensionActivation activation, string providerId, IMediaFrameSource source)
        {
            _owner = owner;
            Activation = activation;
            ProviderId = providerId;
            Source = source;
            Token = Cancellation.Token;
        }
        public void Dispose() => _owner.Remove(this);
    }

    private sealed class Preview
    {
        private readonly CancellationTokenSource _cancellation = new();
        public Guid Id { get; } = Guid.NewGuid();
        public RunMediaContext Context { get; }
        public Registration Registration { get; }
        public CancellationToken Token { get; }
        public long Sequence { get; set; }
        public bool Busy { get; set; }
        public Preview(RunMediaContext context, Registration registration)
        {
            Context = context;
            Registration = registration;
            Token = _cancellation.Token;
        }
        public void Cancel()
        {
            try { _cancellation.Cancel(); }
            finally { _cancellation.Dispose(); }
        }
    }
}

public sealed class MediaPreview : IDisposable
{
    private readonly MediaPreviewRegistry _owner;
    public Guid PreviewId { get; }
    internal MediaPreview(MediaPreviewRegistry owner, Guid previewId) { _owner = owner; PreviewId = previewId; }
    public ValueTask<PreviewImage> CaptureAsync(CancellationToken cancellationToken = default) => _owner.CaptureAsync(PreviewId, cancellationToken);
    public void Dispose() => _owner.Close(PreviewId);
}
