using System.Buffers.Binary;
using System.IO.Compression;
using NexusPipeline.Platform.Media;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class RgbCaptureTests
{
    [Fact(DisplayName = "Canonical Capture Validation Streams Bounded Chunks And Restores The Caller Position")]
    public void StreamsBoundedCapture()
    {
        using var bytes = new MemoryStream(); var random = new Random(719);
        PngScanlineEncoder.Write(bytes, 512, 512, (_, row) => random.NextBytes(row));
        Assert.True(bytes.Length > 65536);
        using var input = new BoundedReads(bytes); input.Position = 17;
        RgbCapturePng.Verify(input, 512, 512);
        Assert.Equal(17, input.Position); Assert.InRange(input.MaximumRead, 1, 16384);
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
        var crc = PngScanlineEncoder.Accumulate(PngScanlineEncoder.Accumulate(uint.MaxValue, type), data);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); output.Write(word);
    }

    private sealed class BoundedReads(Stream original) : Stream
    {
        internal int MaximumRead { get; private set; }
        internal long ReadBytes { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            Assert.InRange(buffer.Length, 0, 16384);
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
