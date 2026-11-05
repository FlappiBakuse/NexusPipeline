using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Media;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledMediaPort(PreparedExtensionActivation source, InstalledExtensionConsumption consumption,
    IMediaPreviewRegistrationPort inner) : IMediaPreviewRegistrationPort
{
    public async ValueTask RegisterAsync(string providerId, IMediaFrameProvider provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider); cancellationToken.ThrowIfCancellationRequested();
        source.RequireBeforeCallback();
        await inner.RegisterAsync(providerId, new Provider(consumption, provider), cancellationToken).ConfigureAwait(false);
        source.RequireBeforeCallback();
    }

    private sealed class Provider(InstalledExtensionConsumption consumption, IMediaFrameProvider provider) : IMediaFrameProvider
    {
        public async ValueTask<MediaFrameContent> CaptureAsync(MediaCapture request, CancellationToken cancellationToken)
        {
            var use = consumption.Begin(); MediaFrameContent? content = null;
            try
            {
                content = await provider.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
                use.RequireCurrent();
                return new(content.Descriptor, new MediaContentStream(content, use));
            }
            catch
            {
                if (content is not null) await content.DisposeAsync().ConfigureAwait(false);
                use.Dispose();
                throw;
            }
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class InstalledScreenshotPort(InstalledExtensionConsumption consumption, IRunScreenshotPort inner)
    : IRunScreenshotPort
{
    public async ValueTask<MediaFrameContent> CaptureAsync(MediaCapture request, CancellationToken cancellationToken)
    {
        var use = consumption.Begin(); MediaFrameContent? content = null;
        try
        {
            content = await inner.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
            use.RequireCurrent();
            return new(content.Descriptor, new MediaContentStream(content, use));
        }
        catch
        {
            if (content is not null) await content.DisposeAsync().ConfigureAwait(false);
            use.Dispose();
            throw;
        }
    }
}
