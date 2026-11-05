using System.Buffers.Binary;
using NexusPipeline.Platform.Media;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class ZlibBoundaryTests
{
    [Theory(DisplayName = "Stored Fixed And Dynamic Single Zlib Streams Keep The Same Rgb Or Rgba Dimensions")]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(0, 6)]
    [InlineData(1, 6)]
    [InlineData(2, 6)]
    public void PreservesStreamDimensions(int block, int color)
    {
        using var png = new MemoryStream(Png(block, color, null), writable: false);
        Assert.Equal(new CapturePngInfo(3, 2, color), BoundedCapturePng.Verify(png, allowRgba: true));
        if (color == 2) RgbCapturePng.Verify(png, 3, 2);
    }

    [Theory(DisplayName = "A Correct Png Crc Cannot Hide Extra Compressed Members Or An Incomplete Adler Trailer")]
    [InlineData(0, "garbage")]
    [InlineData(1, "garbage")]
    [InlineData(2, "garbage")]
    [InlineData(0, "secondMember")]
    [InlineData(1, "secondMember")]
    [InlineData(2, "secondMember")]
    [InlineData(0, "truncatedAdler")]
    [InlineData(1, "truncatedAdler")]
    [InlineData(2, "truncatedAdler")]
    public void RejectsExtraStreams(int block, string fault)
    {
        using var png = new MemoryStream(Png(block, 6, fault), writable: false); png.Position = 7;
        Assert.ThrowsAny<IOException>(() => BoundedCapturePng.Verify(png, allowRgba: true));
        Assert.Equal(7, png.Position);
    }

    private static byte[] Png(int block, int color, string? fault)
    {
        var raw = new byte[(3 * (color == 6 ? 4 : 3) + 1) * 2];
        var compressed = Zlib(block, raw);
        if (fault == "garbage") compressed = [.. compressed, 0x42];
        else if (fault == "secondMember") compressed = [.. compressed, .. Zlib(block, raw)];
        else if (fault == "truncatedAdler") compressed = compressed[..^2];
        using var png = new MemoryStream(); png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, 3);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 2); header[8] = 8; header[9] = (byte)color;
        Chunk(png, "IHDR"u8, header); Chunk(png, "IDAT"u8, compressed); Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static byte[] Zlib(int block, byte[] raw)
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 0x78, 0x01 });
        var bits = new Writer(output); bits.Write(1, 1); bits.Write(block, 2);
        if (block == 0)
        {
            bits.Align(); Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)raw.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(size[2..], (ushort)(ushort.MaxValue ^ raw.Length));
            output.Write(size); output.Write(raw);
        }
        else if (block == 1)
        {
            foreach (var value in raw) bits.Code(0x30 + value, 8);
            bits.Code(0, 7); bits.Align();
        }
        else
        {
            bits.Write(0, 5); bits.Write(0, 5); bits.Write(14, 4);
            for (var index = 0; index < 18; index++) bits.Write(index == 2 || index == 17 ? 2 : index == 3 ? 1 : 0, 3);
            bits.Code(2, 2);
            bits.Code(3, 2); bits.Write(127, 7);
            bits.Code(3, 2); bits.Write(106, 7);
            bits.Code(2, 2); bits.Code(0, 1);
            foreach (var _ in raw) bits.Code(0, 1);
            bits.Code(1, 1); bits.Align();
        }
        uint a = 1, b = 0;
        foreach (var value in raw) { a = (a + value) % 65521; b = (b + a) % 65521; }
        Span<byte> adler = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(adler, (b << 16) | a); output.Write(adler);
        return output.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> bytes)
    {
        Span<byte> word = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(word, bytes.Length);
        output.Write(word); output.Write(kind); output.Write(bytes);
        var crc = PngScanlineEncoder.Accumulate(PngScanlineEncoder.Accumulate(uint.MaxValue, kind), bytes);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); output.Write(word);
    }

    private sealed class Writer(Stream output)
    {
        private int _bits, _count;
        internal void Write(int value, int count)
        {
            for (var index = 0; index < count; index++)
            {
                _bits |= ((value >> index) & 1) << _count++;
                if (_count == 8) Align();
            }
        }
        internal void Code(int value, int count)
        { for (var index = count - 1; index >= 0; index--) Write((value >> index) & 1, 1); }
        internal void Align()
        { if (_count != 0) output.WriteByte((byte)_bits); _bits = 0; _count = 0; }
    }
}
