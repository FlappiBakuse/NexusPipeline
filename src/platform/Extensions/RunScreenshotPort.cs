using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Media;
using NexusPipeline.Extensibility.Media;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

public sealed class RunScreenshotPort : IRunScreenshotPort
{
    private readonly ExtensionScope _scope;
    private readonly IMediaFrameSource _source;
    private readonly IRunMediaAuthorization _authorization;
    private readonly ContractReader _reader;
    private readonly bool _declared;

    public RunScreenshotPort(ExtensionManifest manifest, ExtensionScope scope, IMediaFrameSource source,
        IRunMediaAuthorization authorization, ContractReader reader)
    {
        var admitted = new ExtensionManifestAdmission(reader).ReadCurrent(JsonSerializer.SerializeToUtf8Bytes(manifest), scope.Activation.ExtensionId);
        _scope = scope;
        _source = source;
        _authorization = authorization;
        _reader = reader;
        _declared = admitted.Capabilities.Contains("media.capture", StringComparer.Ordinal);
    }

    public async ValueTask<MediaFrameContent> CaptureAsync(MediaCapture request, CancellationToken cancellationToken)
    {
        if (!_declared) throw new UnauthorizedAccessException("media_capture_capability_required");
        EnsurePublished();
        var admitted = _reader.Read<MediaCapture>(JsonSerializer.SerializeToUtf8Bytes(request), "media-capture.schema.json");
        var context = new RunMediaContext(Guid.Parse(admitted.InstanceId), Guid.Parse(admitted.RunId), Guid.Parse(admitted.AttemptId));
        _authorization.AssertAllowed(context, _scope.Activation.ExtensionId);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _scope.Cancellation);
        cancellation.Token.ThrowIfCancellationRequested();
        var image = await _source.CaptureAsync(new(context, Guid.Parse(admitted.PreviewId), Guid.Parse(admitted.RequestId), admitted.Sequence, _scope.Activation.ExtensionId),
            cancellation.Token).ConfigureAwait(false);
        cancellation.Token.ThrowIfCancellationRequested();
        EnsurePublished();
        _authorization.AssertAllowed(context, _scope.Activation.ExtensionId);
        var bytes = image.Png.ToArray();
        var descriptor = new MediaFrame
        {
            Format = "nexus.media.frame", Version = 1, Capture = admitted, MimeType = "image/png",
            Width = image.Width, Height = image.Height, SizeBytes = bytes.Length,
            CapturedAt = image.CapturedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        };
        return new(descriptor, new MemoryStream(bytes, writable: false));
    }

    private void EnsurePublished()
    {
        _scope.Cancellation.ThrowIfCancellationRequested();
        if (!_scope.IsPublished) throw new InvalidOperationException("extension_activation_not_published");
    }
}
