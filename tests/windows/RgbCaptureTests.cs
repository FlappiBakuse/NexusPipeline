using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using NexusPipeline.Platform.Media;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class RgbCaptureTests
{
    [Fact(DisplayName = "Capture Validation Uses Bounded Memory And Preserves The Original Bytes And Position")]
    public void StreamsBoundedCapture()
    {
        const int size = 512;
        using var bytes = new MemoryStream();
        bytes.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], size);
        header[8] = 8; header[9] = 2;
        WriteChunk(bytes, "IHDR"u8, header);
        using var data = new MemoryStream();
        var row = new byte[1 + size * 3];
        var random = new Random(719);
        using (var zlib = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
            for (var i = 0; i < size; i++)
            {
                random.NextBytes(row.AsSpan(1));
                zlib.Write(row);
            }
        WriteChunk(bytes, "IDAT"u8, data.ToArray());
        WriteChunk(bytes, "IEND"u8, []);
        Assert.InRange(bytes.Length, 768L * 1024, 850L * 1024);
        var original = SHA256.HashData(bytes.ToArray());
        using var input = new BoundedReads(bytes); input.Position = 17;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        RgbCapturePng.Verify(input, 512, 512);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 256L * 1024);
        Assert.Equal(17, input.Position);
        Assert.Equal(original, SHA256.HashData(bytes.ToArray()));
        Assert.True(input.ReadBytes >= bytes.Length);
    }

    [Theory(DisplayName = "Invalid Capture Envelope Or Decompressed Rows Cannot Become A Frame")]
    [InlineData("dimensions")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("crc")]
    [InlineData("deflate")]
    [InlineData("shortRows")]
    [InlineData("extraRows")]
    [InlineData("filter")]
    public void RejectsInvalidEnvelopes(string mutation)
    {
        using var original = new MemoryStream();
        PngScanlineEncoder.Write(original, 32, 24, (_, row) => row.AsSpan().Fill(83));
        var bytes = original.ToArray(); var expectedWidth = 32;
        if (mutation == "dimensions") expectedWidth++;
        else if (mutation == "truncated") bytes = bytes[..^5];
        else if (mutation == "trailing") bytes = [.. bytes, 1];
        else if (mutation == "crc") bytes[29] ^= 1;
        else
        {
            byte[] compressed;
            if (mutation == "deflate") compressed = [0xff, 0xff, 0xff, 0xff];
            else
            {
                var rows = mutation == "shortRows" ? 23 : mutation == "extraRows" ? 25 : 24;
                var raw = new byte[(32 * 3 + 1) * rows];
                if (mutation == "filter") raw[0] = 5;
                using var buffer = new MemoryStream();
                using (var zip = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(raw);
                compressed = buffer.ToArray();
            }
            using var replaced = new MemoryStream(); replaced.Write(bytes.AsSpan(0, 33));
            WriteChunk(replaced, "IDAT"u8, compressed); WriteChunk(replaced, "IEND"u8, []);
            bytes = replaced.ToArray();
        }
        using var input = new MemoryStream(bytes, writable: false); input.Position = 3;
        Assert.ThrowsAny<IOException>(() => RgbCapturePng.Verify(input, expectedWidth, 24));
        Assert.Equal(3, input.Position);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word); output.Write(type); output.Write(data);
        var crc = Checksum(Checksum(uint.MaxValue, type), data);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); output.Write(word);
    }

    private static uint Checksum(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320U;
        }
        return crc;
    }

    private sealed class BoundedReads(Stream original) : Stream
    {
        internal long ReadBytes { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            var read = original.Read(buffer); ReadBytes += read; return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => original.Length;
        public override long Position { get => original.Position; set => original.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => original.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
