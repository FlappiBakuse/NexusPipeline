using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace NexusPipeline.Platform.Media;

internal static class PngScanlineEncoder
{
    internal static void Write(Stream output, int width, int height, Action<int, byte[]> readBgraRow)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 33554432)
            throw new IOException("capture_dimensions_invalid");
        output = new LimitedOutput(output);
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13]; header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; header[9] = 2;
        Chunk(output, "IHDR", header);
        using (var chunks = new IdatChunks(output))
        using (var compressed = new ZLibStream(chunks, CompressionLevel.Fastest, leaveOpen: true))
        {
            var source = new byte[checked(width * 4)]; var row = new byte[checked(width * 3 + 1)];
            for (var y = 0; y < height; y++)
            {
                readBgraRow(y, source);
                row[0] = 0;
                for (var x = 0; x < width; x++)
                { row[x * 3 + 1] = source[x * 4 + 2]; row[x * 3 + 2] = source[x * 4 + 1]; row[x * 3 + 3] = source[x * 4]; }
                compressed.Write(row);
            }
        }
        Chunk(output, "IEND", []);
    }

    private static void Chunk(Stream output, string kind, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(word, data.Length); output.Write(word);
        Span<byte> type = stackalloc byte[4]; Encoding.ASCII.GetBytes(kind, type); output.Write(type); output.Write(data);
        var crc = Accumulate(uint.MaxValue, type); crc = Accumulate(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); output.Write(word);
    }

    internal static uint Accumulate(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return crc;
    }

    private sealed class IdatChunks(Stream output) : Stream
    {
        private readonly byte[] _buffer = new byte[65536];
        private int _used;
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                var take = Math.Min(bytes.Length, _buffer.Length - _used);
                bytes[..take].CopyTo(_buffer.AsSpan(_used)); _used += take; bytes = bytes[take..];
                if (_used == _buffer.Length) Flush();
            }
        }
        public override void Flush() { if (_used != 0) { Chunk(output, "IDAT", _buffer.AsSpan(0, _used)); _used = 0; } }
        protected override void Dispose(bool disposing) { if (disposing) Flush(); base.Dispose(disposing); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class LimitedOutput(Stream output) : Stream
    {
        private long _written;
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > 8388608 - _written) throw new IOException("capture_png_byte_limit");
            output.Write(bytes); _written += bytes.Length;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() => output.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
