using System.Buffers.Binary;
using System.Security.Cryptography;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record CaptureFrameRecord(int Index, Guid FrameId, BlobReference Png);

internal static class CaptureFramePolicy
{
    internal static Guid FrameId(Guid captureId, int index)
    {
        if (captureId == Guid.Empty || index is < 0 or >= ExecutionCaptureInputs.MaximumBatchFrames)
            throw Invalid();
        if (index == 0) return captureId;
        Span<byte> source = stackalloc byte[20];
        captureId.TryWriteBytes(source);
        BinaryPrimitives.WriteInt32LittleEndian(source[16..], index);
        var digest = SHA256.HashData(source);
        return new Guid(digest.AsSpan(0, 16));
    }

    internal static IReadOnlyList<CaptureFrameRecord> Images(CaptureResultDocument result, Guid captureId)
    {
        if (result.Outcome != "captured") return [];
        return result.Frames is { } frames ? frames :
            [new CaptureFrameRecord(0, captureId, result.Png ?? throw Invalid())];
    }

    internal static void RequireBatch(IReadOnlyList<CaptureFrameRecord> frames, Guid captureId,
        int expectedCount, int width, int height)
    {
        if (expectedCount is < 2 or > ExecutionCaptureInputs.MaximumBatchFrames || frames.Count != expectedCount ||
            width is < 1 or > 8192 || height is < 1 or > 8192 ||
            (long)width * height > 33_554_432)
            throw Invalid();
        long bytes = 0;
        var unique = new HashSet<Guid>();
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (frame is null || frame.Index != index || frame.FrameId != FrameId(captureId, index) ||
                !unique.Add(frame.FrameId) || frame.Png is null ||
                frame.Png.SizeBytes is < 57 or > ExecutionCaptureInputs.MaximumPngBytes ||
                frame.Png.RelativePath != $"blobs/content/{frame.Png.Sha256}.bin" ||
                !NexusPipeline.Core.Configuration.FileTreePolicy.Digest(frame.Png.Sha256)) throw Invalid();
            bytes = checked(bytes + frame.Png.SizeBytes);
        }
        if (bytes > ExecutionCaptureInputs.MaximumBatchBytes) throw Invalid();
    }

    private static IOException Invalid() => new("execution_capture_frame_batch_invalid");
}
