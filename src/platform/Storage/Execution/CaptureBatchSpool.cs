using System.Buffers.Binary;
using NexusPipeline.Core.Media;
using NexusPipeline.Platform.Media;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class CaptureBatchSpool(RuntimeInstance instance, Guid captureId, int width, int height, int count) : IDisposable
{
    private readonly List<CaptureFrameRecord> _frames = [];
    private readonly List<CapturePngSpool> _spools = [];
    private readonly ImmutableBlobStore _blobs = new(instance, new());
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
                var spool = new CapturePngSpool(instance, CaptureFramePolicy.FrameId(captureId, index), width, height);
                _spools.Add(spool);
                await spool.ReadLengthAsync(output, length, cancellation).ConfigureAwait(false);
                spool.VerifyFrame();
            }
            if (await output.ReadAsync(new byte[1], cancellation).ConfigureAwait(false) != 0)
                throw new IOException("capture_frame_batch_trailing_bytes");
            for (var index = 0; index < count; index++)
                _frames.Add(new(index, CaptureFramePolicy.FrameId(captureId, index), _spools[index].VerifyAndPersist(_blobs)));
            CaptureFramePolicy.RequireBatch(_frames, captureId, count, width, height);
            _complete = true;
        }
        finally
        {
            foreach (var spool in _spools) spool.Dispose();
            _spools.Clear();
        }
    }

    internal IReadOnlyList<CaptureFrameRecord> Frames()
    {
        if (!_complete) throw new IOException("capture_frame_batch_incomplete");
        return _frames.AsReadOnly();
    }

    internal PreviewImage ReadLiveFrame()
    {
        var first = Frames()[0].Png;
        using var input = _blobs.OpenVerified(first);
        RgbCapturePng.Verify(input, width, height); input.Position = 0;
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return new(width, height, bytes);
    }

    public void Dispose()
    {
        foreach (var spool in _spools) spool.Dispose();
        _spools.Clear();
    }
}
