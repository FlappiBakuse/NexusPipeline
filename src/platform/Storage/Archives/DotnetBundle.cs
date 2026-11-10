using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace NexusPipeline.Platform.Storage.Archives;

internal static class DotnetBundle
{
    private sealed record BundleFile(long Offset, long Size, byte Kind);
    private static readonly byte[] Marker = Convert.FromHexString("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae");
    private const int MaximumImage = 2 * 1024 * 1024;

    internal static byte[] ReadAssembly(Stream input, string name, bool standalone = false)
    {
        try
        {
            if (!input.CanSeek || input.Length is < 64 or > 268435456) throw Invalid();
            var files = ReadFiles(input);
            if (!files.TryGetValue(name, out var file) || file.Kind != 1 || file.Size > 67108864) throw Invalid();
            if (standalone)
            {
                var prefix = Path.GetFileNameWithoutExtension(name);
                if (files.Count != 3 || files.GetValueOrDefault(prefix + ".deps.json") is not { Kind: 3 } ||
                    files.GetValueOrDefault(prefix + ".runtimeconfig.json") is not { Kind: 4 }) throw Invalid();
            }
            input.Position = file.Offset;
            var bytes = new byte[checked((int)file.Size)];
            input.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception error) when (error is BadImageFormatException or EndOfStreamException or
            FormatException or OverflowException or DecoderFallbackException or ArgumentException or InvalidOperationException)
        { throw new IOException("dotnet_bundle_invalid", error); }
    }

    private static Dictionary<string, BundleFile> ReadFiles(Stream input)
    {
        input.Position = 0;
        using var image = new PEReader(input, PEStreamOptions.LeaveOpen);
        var headers = image.PEHeaders;
        if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus ||
            headers.SectionHeaders.Length is < 1 or > 96) throw Invalid();
        var extent = headers.SectionHeaders.Max(section => checked(section.PointerToRawData + section.SizeOfRawData));
        if (extent is < 64 or > MaximumImage || extent > input.Length) throw Invalid();
        input.Position = 0;
        var native = new byte[extent]; input.ReadExactly(native);
        var marker = native.AsSpan().IndexOf(Marker);
        if (marker < 8 || native.AsSpan(marker + 1).IndexOf(Marker) >= 0) throw Invalid();
        var offset = BinaryPrimitives.ReadInt64LittleEndian(native.AsSpan(marker - 8, 8));
        if (offset < extent || offset >= input.Length - 52) throw Invalid();
        input.Position = offset;
        using var reader = new BinaryReader(input, new UTF8Encoding(false, true), leaveOpen: true);
        if (reader.ReadUInt32() != 6 || reader.ReadUInt32() != 0) throw Invalid();
        var count = reader.ReadInt32();
        if (count is < 1 or > 512 || ReadString(reader, 128).Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-'))) throw Invalid();
        var deps = (reader.ReadInt64(), reader.ReadInt64());
        var config = (reader.ReadInt64(), reader.ReadInt64());
        if (reader.ReadUInt64() != 0) throw Invalid();
        var files = new Dictionary<string, BundleFile>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            var start = reader.ReadInt64(); var size = reader.ReadInt64(); var compressed = reader.ReadInt64();
            var kind = reader.ReadByte(); var name = ReadString(reader, 240);
            PhysicalPathPolicy.ValidateRelativePath(name);
            if (!names.Add(name) || kind > 5 || compressed != 0 || start < extent || start > offset ||
                size < 1 || size > offset - start) throw Invalid();
            files.Add(name, new(start, size, kind));
        }
        var ordered = files.Values.OrderBy(file => file.Offset).ToArray();
        if (ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.Offset + pair.First.Size > pair.Second.Offset)) throw Invalid();
        RequireLocation(files, 3, deps); RequireLocation(files, 4, config);
        return files;
    }

    private static string ReadString(BinaryReader reader, int maximum)
    {
        var length = reader.Read7BitEncodedInt();
        if (length < 1 || length > maximum) throw Invalid();
        var bytes = new byte[length]; reader.BaseStream.ReadExactly(bytes);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static void RequireLocation(Dictionary<string, BundleFile> files, byte kind, (long Offset, long Size) location)
    {
        var matching = files.Values.Where(file => file.Kind == kind).ToArray();
        if (matching.Length != 1 || matching[0].Offset != location.Offset || matching[0].Size != location.Size) throw Invalid();
    }

    private static IOException Invalid() => new("dotnet_bundle_invalid");
}
