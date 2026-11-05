using System.Buffers.Binary;
using System.IO.Compression;

internal static class DeviceFrameFixture
{
    internal static void RequirePixels(ReadOnlyMemory<byte> png, bool screen = false)
    {
        using var input = new MemoryStream(png.ToArray());
        input.Position = 8;
        using var compressed = new MemoryStream();
        while (input.Position < input.Length)
        {
            var header = new byte[8]; input.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length < 0 || length > input.Length - input.Position - 4) throw new IOException("device_fixture_png_invalid");
            var bytes = new byte[length]; input.ReadExactly(bytes); input.Position += 4;
            if (header.AsSpan(4).SequenceEqual("IDAT"u8)) compressed.Write(bytes);
            if (header.AsSpan(4).SequenceEqual("IEND"u8)) break;
        }
        compressed.Position = 0;
        using var decoded = new ZLibStream(compressed, CompressionMode.Decompress);
        var row = new byte[640 * 3 + 1];
        var inset = screen ? 16 : 0;
        for (var y = 0; y < 480; y++)
        {
            decoded.ReadExactly(row);
            if (row[0] != 0 || !row.AsSpan(1 + inset * 3, 3).SequenceEqual(new byte[] { 0, 0, 255 }) ||
                !row.AsSpan(1 + (639 - inset) * 3, 3).SequenceEqual(new byte[] { 255, 0, 0 }))
                throw new IOException("device_fixture_pixels_unbound");
        }
    }
}
