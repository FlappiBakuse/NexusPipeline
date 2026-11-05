using System.Text.Json;

namespace NexusPipeline.Platform.Serialization;

public static class BoundedJson
{
    public static byte[] Serialize<T>(T value, JsonSerializerOptions? options = null)
        => Serialize(value, options, StrictJson.MaximumBytes);

    internal static byte[] Serialize<T>(T value, JsonSerializerOptions? options, int maximumBytes)
    {
        if (maximumBytes is < 1 or > 32 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var output = new LimitedOutput(maximumBytes);
        JsonSerializer.Serialize(output, value, options);
        return output.ToArray();
    }

    private sealed class LimitedOutput(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Require(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Require(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Require(1);
            base.WriteByte(value);
        }

        private void Require(int count)
        {
            if (count > maximumBytes - Position) throw new JsonException("invalid_json_size");
        }
    }
}
