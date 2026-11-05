using System.Buffers.Binary;
using System.IO.Compression;

namespace NexusPipeline.Platform.Media;

internal sealed record CapturePngInfo(int Width, int Height, int ColorType);

internal static class BoundedCapturePng
{
    internal static CapturePngInfo Verify(Stream input, bool allowRgba, bool allowAncillary = false)
    {
        if (!input.CanRead || !input.CanSeek || input.Length is < 57 or > CapturePngSpool.MaximumBytes) throw Invalid();
        var position = input.Position;
        try
        {
            input.Position = 0;
            Span<byte> signature = stackalloc byte[8]; input.ReadExactly(signature);
            if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw Invalid();
            var (info, parts) = ReadChunks(input, allowRgba, allowAncillary);
            // Inflater EOF may leave buffered compressed bytes, so verify the member boundary separately.
            using (var boundary = new IdatInput(input, parts))
                PngZlibBoundary.RequireSingleStream(boundary, parts.Sum(part => (long)part.Length),
                    checked((long)info.Height * (info.Width * (info.ColorType == 6 ? 4L : 3L) + 1)));
            using var contents = new IdatInput(input, parts);
            using var raw = new ZLibStream(contents, CompressionMode.Decompress, leaveOpen: true);
            var row = new byte[checked(info.Width * (info.ColorType == 6 ? 4 : 3) + 1)];
            for (var y = 0; y < info.Height; y++)
            {
                raw.ReadExactly(row);
                if (row[0] > 4) throw Invalid();
            }
            if (raw.ReadByte() != -1) throw Invalid();
            return info;
        }
        catch (InvalidDataException error) { throw new IOException("capture_png_invalid", error); }
        finally { input.Position = position; }
    }

    internal static void RequireInfo(CapturePngInfo info)
    {
        if (info.Width is < 1 or > 8192 || info.Height is < 1 or > 8192 ||
            (long)info.Width * info.Height > 33554432 || info.ColorType is not (2 or 6)) throw Invalid();
    }

    private static (CapturePngInfo Info, IReadOnlyList<(long Offset, int Length)> Parts) ReadChunks(
        Stream input, bool allowRgba, bool allowAncillary)
    {
        var parts = new List<(long Offset, int Length)>();
        var header = new byte[8]; var crcWord = new byte[4]; var buffer = new byte[16384];
        var imageHeader = new byte[13]; var chunks = 0; CapturePngInfo? info = null;
        var dataEnded = false; var ancillary = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (++chunks > 4096 || input.Length - input.Position < 12) throw Invalid();
            input.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length < 0 || length > input.Length - input.Position - 4) throw Invalid();
            var kind = header.AsSpan(4, 4);
            if (info is null)
            {
                if (!kind.SequenceEqual("IHDR"u8) || length != 13) throw Invalid();
            }
            else if (kind.SequenceEqual("IDAT"u8))
            {
                if (dataEnded) throw Invalid();
                parts.Add((input.Position, length));
            }
            else if (kind.SequenceEqual("IEND"u8))
            {
                if (length != 0 || parts.Count == 0) throw Invalid();
            }
            else
            {
                if (!allowAncillary || !AllowedAncillary(kind, length, info.ColorType)) throw Invalid();
                var name = System.Text.Encoding.ASCII.GetString(kind);
                if (parts.Count > 0 || !ancillary.Add(name)) throw Invalid();
                dataEnded = parts.Count > 0;
            }
            var crc = PngScanlineEncoder.Accumulate(uint.MaxValue, kind);
            var remaining = length; var consumed = 0;
            while (remaining > 0)
            {
                var take = Math.Min(remaining, buffer.Length); input.ReadExactly(buffer.AsSpan(0, take));
                if (kind.SequenceEqual("IHDR"u8)) buffer.AsSpan(0, take).CopyTo(imageHeader.AsSpan(consumed));
                crc = PngScanlineEncoder.Accumulate(crc, buffer.AsSpan(0, take)); consumed += take; remaining -= take;
            }
            input.ReadExactly(crcWord);
            if (BinaryPrimitives.ReadUInt32BigEndian(crcWord) != ~crc) throw Invalid();
            if (allowAncillary && info is not null && !kind.SequenceEqual("IDAT"u8) && !kind.SequenceEqual("IEND"u8))
                RequireAncillaryValue(kind, buffer.AsSpan(0, length));
            if (info is null)
            {
                info = new(BinaryPrimitives.ReadInt32BigEndian(imageHeader),
                    BinaryPrimitives.ReadInt32BigEndian(imageHeader.AsSpan(4)), imageHeader[9]);
                RequireInfo(info);
                if (imageHeader[8] != 8 || !allowRgba && info.ColorType != 2 ||
                    imageHeader[10] != 0 || imageHeader[11] != 0 || imageHeader[12] != 0) throw Invalid();
            }
            if (!kind.SequenceEqual("IEND"u8)) continue;
            if (input.Position != input.Length || parts.Sum(part => (long)part.Length) == 0) throw Invalid();
            return (info, parts);
        }
    }

    private static bool AllowedAncillary(ReadOnlySpan<byte> kind, int size, int color) =>
        kind.SequenceEqual("sRGB"u8) && size == 1 || kind.SequenceEqual("gAMA"u8) && size == 4 ||
        kind.SequenceEqual("cHRM"u8) && size == 32 || kind.SequenceEqual("pHYs"u8) && size == 9 ||
        kind.SequenceEqual("sBIT"u8) && size == (color == 6 ? 4 : 3);

    private static void RequireAncillaryValue(ReadOnlySpan<byte> kind, ReadOnlySpan<byte> data)
    {
        if (kind.SequenceEqual("sRGB"u8) && data[0] > 3 ||
            kind.SequenceEqual("gAMA"u8) && BinaryPrimitives.ReadUInt32BigEndian(data) == 0 ||
            kind.SequenceEqual("pHYs"u8) && data[8] > 1) throw Invalid();
        if (kind.SequenceEqual("sBIT"u8))
            foreach (var value in data) if (value is < 1 or > 8) throw Invalid();
        if (kind.SequenceEqual("cHRM"u8))
            for (var index = 0; index < 4; index++)
            {
                var x = BinaryPrimitives.ReadUInt32BigEndian(data[(index * 8)..]);
                var y = BinaryPrimitives.ReadUInt32BigEndian(data[(index * 8 + 4)..]);
                if (x > 100000 || y > 100000 || (long)x + y > 100000) throw Invalid();
            }
    }

    private static IOException Invalid() => new("capture_png_invalid");

    private sealed class IdatInput(Stream source, IReadOnlyList<(long Offset, int Length)> parts) : Stream
    {
        private int _index, _position;
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            while (_index < parts.Count)
            {
                var part = parts[_index];
                if (_position == part.Length) { _index++; _position = 0; continue; }
                source.Position = checked(part.Offset + _position);
                var count = source.Read(buffer[..Math.Min(buffer.Length, part.Length - _position)]);
                if (count == 0) throw new EndOfStreamException();
                _position += count; return count;
            }
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
