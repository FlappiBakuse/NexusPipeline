using System.Buffers.Binary;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Execution.Judgement;

internal static class JudgementWorkerFraming
{
    internal static async Task<byte[]?> ReadAsync(Stream input, CancellationToken cancellationToken, int maximumBytes = StrictJson.MaximumBytes)
    {
        var prefix = new byte[4];
        var read = await input.ReadAsync(prefix.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (read == 0) return null;
        try { await input.ReadExactlyAsync(prefix.AsMemory(1), cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException error) { throw new IOException("judgement_worker_frame_incomplete", error); }
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (maximumBytes is < 1 or > JudgementWorkerProtocol.MaximumInvocationBytes || length < 1 || length > maximumBytes)
            throw new IOException("judgement_worker_frame_size_invalid");
        var bytes = new byte[length];
        try { await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException error) { throw new IOException("judgement_worker_frame_incomplete", error); }
        return bytes;
    }

    internal static async Task WriteAsync(Stream output, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken,
        int maximumBytes = StrictJson.MaximumBytes)
    {
        if (maximumBytes is < 1 or > JudgementWorkerProtocol.MaximumInvocationBytes || bytes.Length < 1 || bytes.Length > maximumBytes)
            throw new IOException("judgement_worker_frame_size_invalid");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
