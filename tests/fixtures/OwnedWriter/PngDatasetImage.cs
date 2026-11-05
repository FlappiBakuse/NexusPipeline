using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

internal static class PngDatasetImage
{
    internal static byte[] Create(int number)
    {
        using var output = new MemoryStream(262144);
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 64);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 64);
        header[8] = 8;
        header[9] = 6;
        Chunk(output, "IHDR", header);
        var raw = new byte[64 * (1 + 64 * 4)];
        for (var row = 0; row < 64; row++)
            for (var column = 0; column < 64; column++)
            {
                var offset = row * 257 + 1 + column * 4;
                raw[offset] = (byte)(number + column);
                raw[offset + 1] = (byte)(number / 7 + row);
                raw[offset + 2] = (byte)(row + column);
                raw[offset + 3] = 255;
            }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        Chunk(output, "IDAT", compressed.ToArray());
        var padding = new byte[checked(262144 - (int)output.Length - 24)];
        BinaryPrimitives.WriteInt32LittleEndian(padding, number);
        Chunk(output, "npAD", padding);
        Chunk(output, "IEND", []);
        if (output.Length != 262144) throw new IOException("Fixture PNG size is invalid");
        return output.ToArray();
    }

    private static void Chunk(Stream output, string name, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        output.Write(length);
        var type = Encoding.ASCII.GetBytes(name);
        output.Write(type);
        output.Write(bytes);
        uint crc = 0xffffffff;
        foreach (var value in type.Concat(bytes))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        output.Write(length);
    }
}
