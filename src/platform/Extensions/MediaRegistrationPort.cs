using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Media;
using NexusPipeline.Extensibility.Media;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

public sealed class MediaRegistrationPort : IMediaPreviewRegistrationPort
{
    private readonly ExtensionScope _scope;
    private readonly MediaPreviewRegistry _registry;
    private readonly ContractReader _reader;
    private readonly bool _declared;

    public MediaRegistrationPort(ExtensionManifest manifest, ExtensionScope scope, MediaPreviewRegistry registry, ContractReader reader)
    {
        var admitted = new ExtensionManifestAdmission(reader).ReadCurrent(JsonSerializer.SerializeToUtf8Bytes(manifest), scope.Activation.ExtensionId);
        _scope = scope;
        _registry = registry;
        _reader = reader;
        _declared = admitted.Capabilities.Contains("media.capture", StringComparer.Ordinal);
    }

    public async ValueTask RegisterAsync(string providerId, IMediaFrameProvider provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        cancellationToken.ThrowIfCancellationRequested();
        _scope.Cancellation.ThrowIfCancellationRequested();
        if (!_declared) throw new UnauthorizedAccessException("media_capture_capability_required");
        var registration = _registry.Register(_scope.Activation, providerId, new Source(provider, _reader));
        await _scope.OwnAsync(new Lifetime(registration)).ConfigureAwait(false);
        _scope.Cancellation.ThrowIfCancellationRequested();
    }

    private sealed class Lifetime(IDisposable registration) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { registration.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Source(IMediaFrameProvider provider, ContractReader reader) : IMediaFrameSource
    {
        public async ValueTask<PreviewImage> CaptureAsync(MediaCaptureRequest request, CancellationToken cancellationToken)
        {
            var capture = new MediaCapture
            {
                Format = "nexus.media.capture", Version = 1, InstanceId = request.Context.InstanceId.ToString("D"),
                RunId = request.Context.RunId.ToString("D"), AttemptId = request.Context.AttemptId.ToString("D"),
                PreviewId = request.PreviewId.ToString("D"), RequestId = request.RequestId.ToString("D"), Sequence = request.Sequence
            };
            await using var result = await provider.CaptureAsync(capture, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("media_frame_missing");
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = reader.Read<MediaFrame>(JsonSerializer.SerializeToUtf8Bytes(result.Descriptor), "media-frame.schema.json");
            if (descriptor.Capture != capture) throw new JsonException("media_frame_identity_mismatch");
            var bytes = new byte[checked((int)descriptor.SizeBytes)];
            await result.Content.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var excess = new byte[1];
            if (await result.Content.ReadAsync(excess, cancellationToken).ConfigureAwait(false) != 0)
                throw new InvalidDataException("media_frame_size_mismatch");
            cancellationToken.ThrowIfCancellationRequested();
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != descriptor.Sha256)
                throw new InvalidDataException("media_frame_digest_mismatch");
            VerifyPngHeader(bytes, descriptor);
            return new PreviewImage(checked((int)descriptor.Width), checked((int)descriptor.Height), bytes,
                DateTimeOffset.Parse(descriptor.CapturedAt, System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void VerifyPngHeader(ReadOnlySpan<byte> bytes, MediaFrame descriptor)
        {
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (!bytes[..8].SequenceEqual(signature) || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) != 13 ||
                !bytes[12..16].SequenceEqual("IHDR"u8) || BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]) != descriptor.Width ||
                BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]) != descriptor.Height)
                throw new InvalidDataException("media_png_header_mismatch");
        }
    }
}
