using System.Buffers.Binary;
using NexusPipeline.Core.Media;
using NexusPipeline.Platform.Media;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class CaptureBatchSpool(RuntimeInstance instance, Guid captureId, int width, int height, int count,
    bool preview = false) : IDisposable
{
    private readonly List<CaptureFrameRecord> _frames = [];
    private readonly List<CapturePngSpool> _spools = [];
    private readonly ImmutableBlobStore? _blobs = preview ? null : new(instance, new());
    private PreviewImage? _first;
    private bool _readStarted, _complete;

    internal async Task ReadOutputAsync(Stream output, CancellationToken cancellation)
    {
        if (_readStarted || count is < 2 or > ExecutionCaptureInputs.MaximumBatchFrames ||
            width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 33_554_432)
            throw new IOException("capture_frame_batch_invalid");
        _readStarted = true; long total = 0;
        try
        {
            for (var index = 0; index < count; index++)
            {
                var header = new byte[40];
                await output.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(36));
                if (!header.AsSpan(0, 8).SequenceEqual("NXSCAP02"u8) || new Guid(header.AsSpan(8, 16)) != captureId ||
                    BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24)) != index ||
                    BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28)) != width ||
                    BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(32)) != height ||
                    length < 57 || length > ExecutionCaptureInputs.MaximumPngBytes ||
                    checked(total + length) > ExecutionCaptureInputs.MaximumBatchBytes)
                    throw new IOException("capture_frame_batch_invalid");
                total += length;
                var frameId = CaptureFramePolicy.FrameId(captureId, index);
                if (preview)
                {
                    using var frame = new CapturePngSpool(frameId, width, height);
                    await frame.ReadLengthAsync(output, length, cancellation).ConfigureAwait(false);
                    if (index == 0) _first = frame.ReadLiveFrame();
                    else frame.VerifyFrame();
                    continue;
                }
                var spool = new CapturePngSpool(instance, frameId, width, height);
                _spools.Add(spool);
                await spool.ReadLengthAsync(output, length, cancellation).ConfigureAwait(false);
                spool.VerifyFrame();
            }
            if (await output.ReadAsync(new byte[1], cancellation).ConfigureAwait(false) != 0)
                throw new IOException("capture_frame_batch_trailing_bytes");
            if (_blobs is not null)
            {
                for (var index = 0; index < count; index++)
                    _frames.Add(new(index, CaptureFramePolicy.FrameId(captureId, index), _spools[index].VerifyAndPersist(_blobs)));
                CaptureFramePolicy.RequireBatch(_frames, captureId, count, width, height);
            }
            _complete = true;
        }
        finally
        {
            foreach (var spool in _spools) spool.Dispose();
            _spools.Clear();
            if (!_complete) _first = null;
        }
    }

    internal IReadOnlyList<CaptureFrameRecord> Frames()
    {
        if (!_complete || preview) throw new IOException("capture_frame_batch_incomplete");
        return _frames.AsReadOnly();
    }

    internal PreviewImage ReadLiveFrame()
    {
        if (!_complete || _first is null) throw new IOException("capture_frame_batch_incomplete");
        return _first;
    }

    public void Dispose()
    {
        _first = null;
        foreach (var spool in _spools) spool.Dispose();
        _spools.Clear();
    }
}
