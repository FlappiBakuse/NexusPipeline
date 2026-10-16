using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace NexusPipeline.Platform.Storage;

internal static class BundleResourceReader
{
    internal static byte[] Read(string executable, string assemblyName, string resourceName, int maxBytes)
    {
        byte[] assembly = ReadAssembly(executable, assemblyName, out _);
        using var image = new PEReader(new MemoryStream(assembly, writable: false));
        MetadataReader metadata = image.GetMetadataReader();
        if (metadata.GetString(metadata.GetAssemblyDefinition().Name) != Path.GetFileNameWithoutExtension(assemblyName)) throw new InvalidDataException("Bundled assembly identity mismatch");
        var resources = metadata.ManifestResources.Where(handle => metadata.GetString(metadata.GetManifestResource(handle).Name) == resourceName).ToArray();
        if (resources.Length != 1) throw new InvalidDataException("Embedded resource missing or duplicated");
        ManifestResource resource = metadata.GetManifestResource(resources[0]);
        if (!resource.Implementation.IsNil || resource.Offset > int.MaxValue) throw new InvalidDataException("External embedded resource");
        var directory = image.PEHeaders.CorHeader!.ResourcesDirectory;
        var bytes = image.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size);
        int start = checked((int)resource.Offset);
        if (start > bytes.Length - 4) throw new InvalidDataException("Invalid resource offset");
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(start, 4));
        if (length is < 1 || length > maxBytes || length > bytes.Length - start - 4) throw new InvalidDataException("Invalid resource length");
        return bytes.AsSpan(start + 4, length).ToArray();
    }

    private static byte[] ReadAssembly(string executable, string assemblyName, out string[] bundleFiles)
    {
        using var stream = File.OpenRead(executable);
        if (stream.Length is < 64 or > 256L * 1024 * 1024) throw new InvalidDataException("Invalid bundle size");
        long nativeLength;
        using (var native = new PEReader(stream, PEStreamOptions.LeaveOpen))
        {
            nativeLength = native.PEHeaders.SectionHeaders.Max(section => checked((long)section.PointerToRawData + section.SizeOfRawData));
            if (nativeLength < 64 || nativeLength >= stream.Length) throw new InvalidDataException("Invalid native image bounds");
        }
        stream.Position = 0;
        byte[] prefix = new byte[(int)Math.Min(nativeLength, 1024 * 1024)]; stream.ReadExactly(prefix);
        byte[] marker = Convert.FromHexString("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae");
        int position = prefix.AsSpan().IndexOf(marker);
        if (position < 8 || prefix.AsSpan(position + marker.Length).IndexOf(marker) >= 0) throw new InvalidDataException("Invalid bundle marker");
        long header = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(position - 8, 8));
        if (header < nativeLength || header >= stream.Length) throw new InvalidDataException("Invalid bundle header offset");
        stream.Position = header;
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
        if (reader.ReadUInt32() != 6 || reader.ReadUInt32() != 0) throw new InvalidDataException("Unsupported bundle format");
        int count = reader.ReadInt32(); if (count is < 1 or > 4096) throw new InvalidDataException("Invalid bundle entries");
        ReadText(reader, 128);
        if (reader.ReadBytes(40).Length != 40) throw new EndOfStreamException();
        (long Offset, int Size)? selected = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            long offset = reader.ReadInt64(), size = reader.ReadInt64(), compressed = reader.ReadInt64();
            byte type = reader.ReadByte(); string name = ReadText(reader, 512);
            if (!names.Add(name) || offset < 0 || size < 0 || compressed != 0 || size > header - offset)
                throw new InvalidDataException("Invalid bundle entry");
            if (name == assemblyName)
            {
                if (type != 1 || size > 64 * 1024 * 1024) throw new InvalidDataException("Invalid bundled assembly");
                selected = (offset, checked((int)size));
            }
        }
        if (selected is not { } entry) throw new InvalidDataException("Bundled assembly missing");
        stream.Position = entry.Offset; byte[] assembly = new byte[entry.Size]; stream.ReadExactly(assembly);
        bundleFiles = names.Order(StringComparer.Ordinal).ToArray();
        return assembly;
    }

    internal static object Describe(string executable, string assemblyName)
    {
        byte[] assembly = ReadAssembly(executable, assemblyName, out var bundleFiles);
        using var image = new PEReader(new MemoryStream(assembly, false));
        var metadata = image.GetMetadataReader();
        string name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        int token = image.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress;
        string? entryPoint = null;
        if ((token & unchecked((int)0xff000000)) == 0x06000000)
        {
            var method = metadata.GetMethodDefinition(System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle(token & 0xffffff));
            var type = metadata.GetTypeDefinition(method.GetDeclaringType());
            entryPoint = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name) + "." + metadata.GetString(method.Name);
        }
        return new { assemblyName = name, entryPoint, bundleFiles,
            references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).Order(StringComparer.Ordinal).ToArray(),
            resources = metadata.ManifestResources.Select(handle => metadata.GetString(metadata.GetManifestResource(handle).Name)).Order(StringComparer.Ordinal).ToArray() };
    }

    private static string ReadText(BinaryReader reader, int maximum)
    {
        int length = reader.Read7BitEncodedInt();
        if (length < 0 || length > maximum) throw new InvalidDataException("Bundle text exceeds limit");
        byte[] bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
