using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace NexusPipeline.Platform.Extensions;

internal sealed record ExtensionZipHeader(string FullName, long Length, long CompressedLength, uint Crc32);

internal static class ExtensionZipMetadata
{
    internal const int MaximumEntries = 4096;
    internal const int MaximumDirectoryBytes = 16777216;
    internal static ImmutableArray<ExtensionZipHeader> Read(Stream input, CancellationToken cancellation,
        long maximumPackageBytes = ExtensionPackageArchive.MaximumPackageBytes)
    {
        if (!input.CanSeek || !input.CanRead || input.Length < 22 || input.Length > maximumPackageBytes) throw Invalid();
        var tail = new byte[checked((int)Math.Min(input.Length, 65557))];
        input.Position = input.Length - tail.Length; input.ReadExactly(tail);
        cancellation.ThrowIfCancellationRequested();
        var end = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
            if (U32(tail, index) == 0x06054b50 && index + 22 + U16(tail, index + 20) == tail.Length)
            { end = index; break; }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 ||
            U16(tail, end + 8) != U16(tail, end + 10)) throw Invalid();
        var count = U16(tail, end + 10);
        var bytes = U32(tail, end + 12); var start = U32(tail, end + 16);
        var endPosition = input.Length - tail.Length + end;
        if (count is 0 or > MaximumEntries || count == ushort.MaxValue || bytes is 0 or > MaximumDirectoryBytes ||
            start == uint.MaxValue || (long)start + bytes != endPosition || start < 4) throw Invalid();
        var first = new byte[4]; input.Position = 0; input.ReadExactly(first);
        if (U32(first, 0) != 0x04034b50) throw Invalid();
        var headers = ImmutableArray.CreateBuilder<ExtensionZipHeader>(count);
        input.Position = start; var header = new byte[46];
        var ranges = new List<(long Start, long End)>();
        for (var index = 0; index < count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (input.Position + header.Length > endPosition) throw Invalid();
            input.ReadExactly(header);
            if (U32(header, 0) != 0x02014b50 || U16(header, 6) > 20 || (U16(header, 8) & ~0x080e) != 0 ||
                (U16(header, 8) & 6) != 0 && U16(header, 10) != 8 ||
                U16(header, 10) is not (0 or 8) || U16(header, 34) != 0) throw Invalid();
            var compressed = U32(header, 20); var size = U32(header, 24); var local = U32(header, 42);
            var nameLength = U16(header, 28); var extraLength = U16(header, 30); var commentLength = U16(header, 32);
            if (compressed == uint.MaxValue || size == uint.MaxValue || local == uint.MaxValue ||
                size > maximumPackageBytes || compressed > maximumPackageBytes ||
                local >= start || (long)local + 30 > start || nameLength is < 1 or > 4096 ||
                size > 0 && (compressed == 0 || size > (long)compressed * ExtensionPackageArchive.MaximumCompressionRatio) ||
                input.Position + nameLength + extraLength + commentLength > endPosition) throw Invalid();
            var name = new byte[nameLength]; input.ReadExactly(name);
            if (name.Contains((byte)0) || (U16(header, 8) & 0x0800) == 0 && name.Any(value => value >= 128)) throw Invalid();
            string decodedName;
            try { decodedName = new UTF8Encoding(false, true).GetString(name); }
            catch (DecoderFallbackException error) { throw new IOException("extension_package_path_encoding_invalid", error); }
            var extra = new byte[extraLength]; input.ReadExactly(extra); RequireExtra(extra);
            input.Position += commentLength;
            var resume = input.Position;
            var finish = RequireLocal(input, header, name, start);
            if (ranges.Any(range => local < range.End && finish > range.Start)) throw Invalid();
            ranges.Add((local, finish)); input.Position = resume;
            headers.Add(new(decodedName, size, compressed, U32(header, 16)));
        }
        if (input.Position != endPosition) throw Invalid();
        long covered = 0;
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            if (range.Start != covered) throw Invalid();
            covered = range.End;
        }
        if (covered != start) throw Invalid();
        input.Position = 0; return headers.ToImmutable();
    }

    private static long RequireLocal(Stream input, byte[] central, byte[] name, long boundary)
    {
        input.Position = U32(central, 42); var local = new byte[30]; input.ReadExactly(local);
        if (U32(local, 0) != 0x04034b50 || U16(local, 4) != U16(central, 6) ||
            U16(local, 6) != U16(central, 8) || U16(local, 8) != U16(central, 10) ||
            U16(local, 26) != name.Length || input.Position + name.Length + U16(local, 28) > boundary) throw Invalid();
        var actualName = new byte[name.Length]; input.ReadExactly(actualName);
        if (!name.AsSpan().SequenceEqual(actualName)) throw Invalid();
        var extra = new byte[U16(local, 28)]; input.ReadExactly(extra); RequireExtra(extra);
        var end = input.Position + U32(central, 20);
        if (end > boundary) throw Invalid();
        if ((U16(local, 6) & 8) == 0)
        {
            if (U32(local, 14) != U32(central, 16) || U32(local, 18) != U32(central, 20) ||
                U32(local, 22) != U32(central, 24)) throw Invalid();
            return end;
        }
        if (U32(local, 14) != 0 && U32(local, 14) != U32(central, 16) ||
            U32(local, 18) != 0 && U32(local, 18) != U32(central, 20) ||
            U32(local, 22) != 0 && U32(local, 22) != U32(central, 24)) throw Invalid();
        input.Position = end;
        if (end + 12 > boundary) throw Invalid();
        var descriptor = new byte[12]; input.ReadExactly(descriptor);
        if (U32(descriptor, 0) == 0x08074b50 && end + 16 <= boundary)
        {
            input.Position = end + 4; var signed = new byte[12]; input.ReadExactly(signed);
            if (DescriptorMatches(signed, central)) return end + 16;
        }
        if (!DescriptorMatches(descriptor, central)) throw Invalid();
        return end + 12;
    }

    private static bool DescriptorMatches(byte[] value, byte[] central) =>
        U32(value, 0) == U32(central, 16) && U32(value, 4) == U32(central, 20) && U32(value, 8) == U32(central, 24);

    private static void RequireExtra(byte[] bytes)
    {
        for (var offset = 0; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 4) throw Invalid();
            var tag = U16(bytes, offset); var length = U16(bytes, offset + 2);
            if (tag is 1 or 0x9901 || offset + 4 + length > bytes.Length) throw Invalid();
            offset += 4 + length;
        }
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static IOException Invalid() => new("extension_package_zip_metadata_invalid");
}
