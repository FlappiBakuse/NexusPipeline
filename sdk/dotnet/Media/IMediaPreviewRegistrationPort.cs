using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Extensibility.Media;

/// <summary>Registers providers in the activation lifetime bound by the Host composition root.</summary>
public interface IMediaPreviewRegistrationPort
{
    /// <summary>Registration is invisible until activation succeeds; the Host owns its cleanup.</summary>
    ValueTask RegisterAsync(string providerId, IMediaFrameProvider provider, CancellationToken cancellationToken);
}

/// <summary>Produces one requested frame without starting a background capture loop.</summary>
public interface IMediaFrameProvider
{
    /// <summary>The response must echo the exact request; cancellation or a closed preview rejects late output.</summary>
    ValueTask<MediaFrameContent> CaptureAsync(MediaCapture request, CancellationToken cancellationToken);
}

/// <summary>Transfers ownership of a bounded PNG stream to the Host, including on invalid output.</summary>
public sealed class MediaFrameContent(MediaFrame descriptor, Stream content) : IAsyncDisposable
{
    /// <summary>Current generated wire descriptor; paths and arbitrary URLs are not accepted.</summary>
    public MediaFrame Descriptor { get; } = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    /// <summary>The Host reads at most the declared bounded size and one excess byte.</summary>
    public Stream Content { get; } = content ?? throw new ArgumentNullException(nameof(content));
    /// <summary>Disposes the transferred stream.</summary>
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
