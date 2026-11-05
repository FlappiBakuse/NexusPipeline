using System.Buffers.Binary;
using System.Text.Json;

namespace NexusPipeline.Extensibility.Execution;

/// <summary>Length-prefixed provider control frames; stdout and stderr remain ordinary raw output.</summary>
public static class ExecutionProviderFrames
{
    /// <summary>Maximum bootstrap size; provider events use the smaller event limit.</summary>
    public const int MaximumBootstrapBytes = 2097152;
    /// <summary>Maximum encoded event size.</summary>
    public const int MaximumEventBytes = 16384;
    /// <summary>Maximum complete control transcript, including each frame prefix.</summary>
    public const int MaximumTranscriptBytes = 2097152;
    /// <summary>Space retained for a terminal event and its frame prefix.</summary>
    public const int TerminalReserveBytes = MaximumEventBytes + 4;
    /// <summary>Host-to-worker control frame requesting cooperative cleanup before native termination.</summary>
    public const byte StopRequest = 2;

    /// <summary>Reads one complete bounded frame, rejecting incomplete headers and bodies.</summary>
    public static async Task<byte[]> ReadAsync(Stream input, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or > MaximumBootstrapBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var prefix = new byte[4];
        await input.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 || length > maximumBytes) throw new IOException("execution_provider_frame_limit");
        var bytes = new byte[length];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>Writes one complete bounded frame. Callers serialize concurrent writes.</summary>
    public static async Task WriteAsync(Stream output, ReadOnlyMemory<byte> bytes, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or > MaximumBootstrapBytes || bytes.Length is < 1 || bytes.Length > maximumBytes)
            throw new IOException("execution_provider_frame_limit");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encodes current generated provider wire types with the declared byte limit.</summary>
    public static byte[] Encode<T>(T value, int maximumBytes)
    {
        if (maximumBytes is < 1 or > MaximumBootstrapBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var output = new BoundedBuffer(maximumBytes);
        using (var writer = new Utf8JsonWriter(output, new() { MaxDepth = 32 }))
            JsonSerializer.Serialize(writer, value);
        if (output.Length is < 1 || output.Length > maximumBytes) throw new IOException("execution_provider_frame_limit");
        return output.ToArray();
    }
    private sealed class BoundedBuffer(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Require(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Require(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Require(1); base.WriteByte(value); }
        private void Require(int count)
        {
            if (Position + count > maximum) throw new IOException("execution_provider_frame_limit");
        }
    }
}
