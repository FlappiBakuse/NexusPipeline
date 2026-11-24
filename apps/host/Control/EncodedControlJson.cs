using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Host.Control;

[JsonConverter(typeof(EncodedControlJson.Converter))]
internal sealed class EncodedControlJson(byte[] ownedUtf8)
{
    // The producer transfers a complete, valid UTF-8 JSON value for the lifetime of the response.
    private readonly byte[] _utf8 = ownedUtf8;
    internal int SizeBytes => _utf8.Length;

    private sealed class Converter : JsonConverter<EncodedControlJson>
    {
        public override EncodedControlJson Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, EncodedControlJson value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value._utf8, skipInputValidation: true);
    }
}

internal sealed class ControlJsonBuffer(int capacity, Func<Exception> invalidSize) : IBufferWriter<byte>, IDisposable
{
    private readonly byte[] _bytes = ArrayPool<byte>.Shared.Rent(capacity);
    private int _written;
    internal ReadOnlyMemory<byte> Written => _bytes.AsMemory(0, _written);

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (sizeHint < 0 || Math.Max(1, sizeHint) > capacity - _written) throw invalidSize();
        return _bytes.AsMemory(_written, capacity - _written);
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public void Advance(int count)
    {
        if (count < 0 || count > capacity - _written) throw invalidSize();
        _written += count;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_bytes);
        ArrayPool<byte>.Shared.Return(_bytes);
    }
}
