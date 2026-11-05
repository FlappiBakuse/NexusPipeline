using System.Buffers.Binary;
using System.IO.Compression;
using NexusPipeline.Platform.Media;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class BoundedPngTests
{
    [Theory(DisplayName = "Exact Rgb Or Rgba Bytes Remain Unchanged And The Caller Position Is Restored")]
    [InlineData(2)]
    [InlineData(6)]
    public void PreservesPixelBytes(int color)
    {
        var bytes = Image(color);
        using var input = new MemoryStream(bytes, writable: false); input.Position = 11;
        Assert.Equal(new CapturePngInfo(3, 2, color), BoundedCapturePng.Verify(input, allowRgba: true));
        Assert.Equal(11, input.Position); Assert.Equal(bytes, input.ToArray());
        if (color == 6) Assert.Throws<IOException>(() => RgbCapturePng.Verify(input, 3, 2));
        else RgbCapturePng.Verify(input, 3, 2);
    }

    [Theory(DisplayName = "Unsupported Or Incomplete Png Never Grants Observed Dimensions")]
    [InlineData("depth")]
    [InlineData("color")]
    [InlineData("interlace")]
    [InlineData("filter")]
    [InlineData("extraRow")]
    [InlineData("trailing")]
    public void RejectsIncompletePng(string change)
    {
        var bytes = Image(6, change);
        using var input = new MemoryStream(bytes, writable: false);
        Assert.ThrowsAny<IOException>(() => BoundedCapturePng.Verify(input, allowRgba: true));
    }

    private static byte[] Image(int color, string? change = null)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 3); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 2);
        header[8] = change == "depth" ? (byte)16 : (byte)8;
        header[9] = change == "color" ? (byte)3 : (byte)color;
        header[12] = change == "interlace" ? (byte)1 : (byte)0;
        Chunk(output, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < (change == "extraRow" ? 3 : 2); y++)
            {
                zip.WriteByte(change == "filter" ? (byte)5 : (byte)0);
                zip.Write(Enumerable.Range(0, 3 * (color == 6 ? 4 : 3)).Select(index => (byte)(index * 17 + y)).ToArray());
            }
        }
        Chunk(output, "IDAT"u8, compressed.ToArray()); Chunk(output, "IEND"u8, []);
        if (change == "trailing") output.WriteByte(1);
        return output.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> bytes)
    {
        Span<byte> word = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(word, bytes.Length);
        output.Write(word); output.Write(kind); output.Write(bytes);
        var crc = PngScanlineEncoder.Accumulate(PngScanlineEncoder.Accumulate(uint.MaxValue, kind), bytes);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); output.Write(word);
    }
}
