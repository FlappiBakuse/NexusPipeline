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
            var bytes = image.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size);
            var terminated = false;
            for (var offset = 0; offset + width <= bytes.Length; offset += width)
            {
                var descriptor = bytes.AsSpan(offset, width);
                if (descriptor.IndexOfAnyExcept((byte)0) < 0) { terminated = true; break; }
                if (delay && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(descriptor) != 1)
                    throw Invalid();
                var rva = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(descriptor[nameOffset..]);
                if (rva <= 0) throw Invalid();
                var block = image.GetSectionData(rva);
                var nameBytes = block.GetContent(0, Math.Min(block.Length, 256));
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

    private static IOException Invalid() => new("extension_native_image_invalid");
}
