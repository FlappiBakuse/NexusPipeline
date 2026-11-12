using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Text;

namespace NexusPipeline.Platform.Extensions;

internal static class InstalledNativeImports
{
    internal static ImmutableArray<string> Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var image = new PEReader(input);
        var header = image.PEHeaders.PEHeader ?? throw Invalid();
        if (image.HasMetadata || image.PEHeaders.CoffHeader.Machine != Machine.Amd64 ||
            (image.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0 ||
            header.Magic != PEMagic.PE32Plus) throw Invalid();
        var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        ReadTable(header.ImportTableDirectory, 20, 12, delay: false);
        ReadTable(header.DelayImportTableDirectory, 32, 4, delay: true);
        return names.ToImmutableArray();

        void ReadTable(DirectoryEntry directory, int width, int nameOffset, bool delay)
        {
            if (directory.RelativeVirtualAddress == 0 && directory.Size == 0) return;
            if (directory.RelativeVirtualAddress <= 0 || directory.Size < width || directory.Size / width > 256)
                throw Invalid();
            var bytes = ReadRva(input, image.PEHeaders, directory.RelativeVirtualAddress, directory.Size, exact: true);
            var terminated = false;
            for (var offset = 0; offset + width <= bytes.Length; offset += width)
            {
                var descriptor = bytes.AsSpan(offset, width);
                if (descriptor.IndexOfAnyExcept((byte)0) < 0) { terminated = true; break; }
                if (delay && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(descriptor) != 1)
                    throw Invalid();
                var rva = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(descriptor[nameOffset..]);
                if (rva <= 0) throw Invalid();
                var nameBytes = ReadRva(input, image.PEHeaders, rva, 256, exact: false);
                var length = nameBytes.AsSpan().IndexOf((byte)0);
                if (length is < 5 or > 255) throw Invalid();
                var name = Encoding.ASCII.GetString(nameBytes.AsSpan(0, length));
                if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
                    throw Invalid();
                names.Add(name);
            }
            if (!terminated) throw Invalid();
        }
    }

    private static byte[] ReadRva(FileStream input, PEHeaders headers, int rva, int maximum, bool exact)
    {
        var sections = headers.SectionHeaders.Where(section => section.VirtualAddress >= 0 && rva >= section.VirtualAddress &&
            (long)rva < (long)section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData)).Take(2).ToArray();
        if (sections.Length != 1) throw Invalid();
        var section = sections[0];
        var relative = (long)rva - section.VirtualAddress;
        var available = section.SizeOfRawData - relative;
        if (available <= 0 || exact && maximum > available) throw Invalid();
        var count = checked((int)Math.Min(maximum, available));
        var offset = section.PointerToRawData + relative;
        if (offset < 0 || offset + count > input.Length) throw Invalid();
        // GetSectionData materializes the whole PE image; dependency discovery needs these bounded ranges only.
        var bytes = new byte[count];
        input.Position = offset; input.ReadExactly(bytes);
        return bytes;
    }

    private static IOException Invalid() => new("extension_native_image_invalid");
}
