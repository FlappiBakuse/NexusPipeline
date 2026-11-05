using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Platform.Security;

internal static class SecretPayloadCodec
{
    private const int HeaderSize = 84;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static ReadOnlySpan<byte> Magic => "nexus-secret-v1!"u8;

    internal static byte[] Encode(Guid instanceId, Guid secretId, SecretValue value)
    {
        byte[]? payload = null;
        try
        {
            value.Use(text =>
            {
                var length = Utf8.GetByteCount(text);
                payload = new byte[checked(HeaderSize + length)];
                Magic.CopyTo(payload);
                instanceId.TryWriteBytes(payload.AsSpan(16, 16), bigEndian: true, out _);
                secretId.TryWriteBytes(payload.AsSpan(32, 16), bigEndian: true, out _);
                BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(48, 4), length);
                Utf8.GetBytes(text, payload.AsSpan(HeaderSize));
                SHA256.HashData(payload.AsSpan(HeaderSize), payload.AsSpan(52, 32));
            });
            return payload ?? throw new IOException("secret_unavailable");
        }
        catch
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            throw;
        }
    }

    internal static SecretValue Decode(Guid instanceId, Guid secretId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < HeaderSize + 1 or > HeaderSize + 262144 || !payload[..16].SequenceEqual(Magic) ||
            new Guid(payload.Slice(16, 16), bigEndian: true) != instanceId || new Guid(payload.Slice(32, 16), bigEndian: true) != secretId ||
            BinaryPrimitives.ReadInt32BigEndian(payload.Slice(48, 4)) != payload.Length - HeaderSize)
            throw new IOException("secret_payload_unrecognized");
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(payload[HeaderSize..], digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, payload.Slice(52, 32))) throw new IOException("secret_payload_integrity_failure");
        char[]? characters = null;
        try
        {
            var count = Utf8.GetCharCount(payload[HeaderSize..]);
            if (count is < 1 or > 65536) throw new IOException("secret_payload_size_invalid");
            characters = new char[count];
            Utf8.GetChars(payload[HeaderSize..], characters);
            return new SecretValue(characters);
        }
        catch (DecoderFallbackException) { throw new IOException("secret_payload_encoding_invalid"); }
        finally { if (characters is not null) Array.Clear(characters); }
    }
}
