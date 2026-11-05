using System.Buffers.Binary;
using System.Security.Cryptography;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Media;

internal sealed class CapturePngSpool : IDisposable
{
    private readonly FileStream _stream;
    private readonly StableDirectoryLease _directory;
    private readonly int? _width, _height;
    private readonly Guid _requestId;
    private bool _complete, _readStarted, _verified;
    internal const long MaximumBytes = 8388608;

    internal CapturePngSpool(RuntimeInstance instance, Guid requestId) : this(instance, requestId, null, null) { }

    internal CapturePngSpool(RuntimeInstance instance, Guid requestId, int width, int height) : this(instance, requestId, (int?)width, height) { }

    private CapturePngSpool(RuntimeInstance instance, Guid requestId, int? width, int? height)
    {
        if (requestId == Guid.Empty || width.HasValue != height.HasValue || width is < 1 or > 8192 || height is < 1 or > 8192 ||
            (long?)width * height > 33554432) throw new ArgumentException("capture_spool_bounds_invalid");
        _requestId = requestId; _width = width; _height = height;
        var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "blobs", "content"));
        Directory.CreateDirectory(directory); _directory = StableDirectoryLease.Acquire(directory);
        try
        {
            // DeleteOnClose also removes the private spool when a crashed owner loses its kernel handles.
            _stream = new(Path.Combine(directory, ".capture-" + requestId.ToString("N") + ".tmp"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        }
        catch { _directory.Dispose(); throw; }
    }

    internal async Task ReadOutputAsync(Stream output, CancellationToken cancellation)
    {
        if (_readStarted) throw new IOException("capture_spool_already_written");
        _readStarted = true;
        if (_width is not null)
        {
            var prefix = new byte[32]; await output.ReadExactlyAsync(prefix, cancellation).ConfigureAwait(false);
            if (!prefix.AsSpan(0, 8).SequenceEqual("NXSCAP01"u8) || new Guid(prefix.AsSpan(8, 16)) != _requestId ||
                BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(24)) != _width ||
                BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(28)) != _height) throw new IOException("capture_worker_frame_unbound");
        }
        var buffer = new byte[16384];
        while (true)
        {
            var count = await output.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (count == 0) break;
            if (_stream.Length + count > MaximumBytes) throw new IOException("capture_worker_frame_byte_limit");
            await _stream.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
        }
        await _stream.FlushAsync(cancellation).ConfigureAwait(false); _complete = true;
    }

    internal async Task ReadLengthAsync(Stream output, int length, CancellationToken cancellation)
    {
        if (_readStarted || length < 57 || length > MaximumBytes) throw new IOException("capture_frame_length_invalid");
        _readStarted = true;
        var buffer = new byte[16384]; var remaining = length;
        while (remaining > 0)
        {
            var count = await output.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellation).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("capture_frame_incomplete");
            await _stream.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
            remaining -= count;
        }
        await _stream.FlushAsync(cancellation).ConfigureAwait(false);
        _complete = true;
    }

    internal BlobReference VerifyAndPersist(ImmutableBlobStore blobs)
    {
        VerifyFrame();
        _stream.Position = 0; var digest = Convert.ToHexStringLower(SHA256.HashData(_stream)); _stream.Position = 0;
        var reference = new BlobReference($"blobs/content/{digest}.bin", _stream.Length, digest);
        blobs.PersistContent(_stream, reference, CancellationToken.None); return reference;
    }

    internal void VerifyFrame()
    {
        if (!_complete) throw new IOException("capture_worker_output_incomplete");
        if (_verified) return;
        _stream.Position = 0;
        if (_width is { } width) RgbCapturePng.Verify(_stream, width, _height!.Value);
        else _ = BoundedCapturePng.Verify(_stream, allowRgba: true, allowAncillary: true);
        _stream.Position = 0;
        _verified = true;
    }

    internal NexusPipeline.Core.Media.PreviewImage ReadLiveFrame()
    {
        if (!_complete) throw new IOException("capture_worker_output_incomplete");
        _stream.Position = 0;
        CapturePngInfo image;
        if (_width is { } width)
        {
            RgbCapturePng.Verify(_stream, width, _height!.Value);
            image = new(width, _height.Value, 2);
        }
        else image = BoundedCapturePng.Verify(_stream, allowRgba: true, allowAncillary: true);
        _stream.Position = 0;
        var bytes = new byte[checked((int)_stream.Length)];
        _stream.ReadExactly(bytes);
        return new(image.Width, image.Height, bytes);
    }

    internal CapturePngInfo ReadVerifiedInfo()
    {
        if (!_complete || _width is not null) throw new IOException("capture_worker_output_incomplete");
        return BoundedCapturePng.Verify(_stream, allowRgba: true, allowAncillary: true);
    }

    public void Dispose() { _stream.Dispose(); _directory.Dispose(); }
}
