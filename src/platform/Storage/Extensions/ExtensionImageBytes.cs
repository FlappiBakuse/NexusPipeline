using System.Buffers.Binary;
using System.Security.Cryptography;
using NexusPipeline.Extensibility.State;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionImageBytes
{
    internal static ExtensionImageInfo Inspect(string contentType, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 12 or > 8388608) throw new IOException("extension_image_size_invalid");
        var size = contentType switch
        {
            "image/png" => Png(bytes), "image/jpeg" => Jpeg(bytes), "image/webp" => Webp(bytes),
            _ => throw new IOException("extension_image_type_invalid")
        };
        if (size.Width is < 1 or > 8192 || size.Height is < 1 or > 8192)
            throw new IOException("extension_image_dimensions_invalid");
        return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), contentType, bytes.Length, size.Width, size.Height);
    }

    internal static void RequireId(string id)
    {
        if (id is not { Length: 64 } || id.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("extension_image_identity_invalid");
    }

    private static (int Width, int Height) Png(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual<byte>([137, 80, 78, 71, 13, 10, 26, 10]) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new IOException("extension_image_format_invalid");
        return (checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[16..])),
            checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[20..])));
    }

    private static (int Width, int Height) Jpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes[0] != 255 || bytes[1] != 216) throw new IOException("extension_image_format_invalid");
        var offset = 2;
        while (offset < bytes.Length)
        {
            if (bytes[offset++] != 255) break;
            while (offset < bytes.Length && bytes[offset] == 255) offset++;
            if (offset == bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0 or 0xda or 0xd9) break;
            if (marker is 1 or >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || offset + length > bytes.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (length < 8) break;
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]));
            }
            offset += length;
        }
        throw new IOException("extension_image_format_invalid");
    }

    private static (int Width, int Height) Webp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length - 8)
            throw new IOException("extension_image_format_invalid");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        if (length > bytes.Length - 20) throw new IOException("extension_image_format_invalid");
        if (bytes.Slice(12, 4).SequenceEqual("VP8X"u8) && length == 10)
            return (1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16),
                1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16));
        if (bytes.Slice(12, 4).SequenceEqual("VP8L"u8) && length >= 5 && bytes[20] == 0x2f)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
            return (1 + (int)(value & 0x3fff), 1 + (int)((value >> 14) & 0x3fff));
        }
        if (bytes.Slice(12, 4).SequenceEqual("VP8 "u8) && length >= 10 && (bytes[20] & 1) == 0 &&
            bytes.Slice(23, 3).SequenceEqual<byte>([0x9d, 1, 0x2a]))
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3fff,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3fff);
        throw new IOException("extension_image_format_invalid");
    }
}
