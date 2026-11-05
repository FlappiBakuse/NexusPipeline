using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace NexusPipeline.StorageTests;

internal static class SdkCaseFixture
{
    internal static void RewriteReference(string path, string expected)
        => Rewrite(path, expected, reader => reader.AssemblyReferences.Select(handle => reader.GetAssemblyReference(handle).Name)
            .Single(handle => reader.GetString(handle) == expected));

    internal static void RewriteDefinition(string path, string expected)
        => Rewrite(path, expected, reader => reader.GetAssemblyDefinition().Name);

    private static void Rewrite(string path, string expected, Func<MetadataReader, StringHandle> select)
    {
        var bytes = File.ReadAllBytes(path);
        var before = (byte[])bytes.Clone();
        int offset;
        StringHandle selected;
        var original = Encoding.UTF8.GetBytes(expected);
        var changed = Encoding.UTF8.GetBytes(expected.ToLowerInvariant());
        if (original.Length != changed.Length || original.AsSpan().SequenceEqual(changed))
            throw new IOException("fixture_sdk_name_not_same_length_case_change");
        using (var stream = new MemoryStream(bytes, writable: false))
        using (var pe = new PEReader(stream))
        {
            var metadata = pe.GetMetadataReader(); var handle = select(metadata); selected = handle;
            if (metadata.GetString(handle) != expected || pe.PEHeaders.MetadataStartOffset < 0)
                throw new IOException("fixture_sdk_metadata_unbound");
            offset = checked(pe.PEHeaders.MetadataStartOffset +
                metadata.GetHeapMetadataOffset(HeapIndex.String) + MetadataTokens.GetHeapOffset(handle));
            if (offset < 0 || offset > bytes.Length - original.Length - 1 ||
                !bytes.AsSpan(offset, original.Length).SequenceEqual(original) || bytes[offset + original.Length] != 0)
                throw new IOException("fixture_sdk_heap_range_unbound");
        }
        changed.CopyTo(bytes, offset);
        for (var index = 0; index < bytes.Length; index++)
            if ((index < offset || index >= offset + changed.Length) && bytes[index] != before[index])
                throw new IOException("fixture_sdk_change_outside_selected_heap_string");
        using (var stream = new MemoryStream(bytes, writable: false))
        using (var pe = new PEReader(stream))
            if (pe.GetMetadataReader().GetString(selected) != expected.ToLowerInvariant())
                throw new IOException("fixture_sdk_metadata_change_unconfirmed");
        File.WriteAllBytes(path, bytes);
    }
}
