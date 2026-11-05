using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility.Lifecycle;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class SdkSurfaceTests
{
    [Theory(DisplayName = "The Actual Compiled Sdk Surface Detects Public Metadata Changes Without Fingerprinting Private Names")]
    [InlineData(true)]
    [InlineData(false)]
    public void ChecksPublicMetadata(bool publicChange)
    {
        var path = typeof(ExtensionActivationContext).Assembly.Location;
        var original = File.ReadAllBytes(path);
        var before = PublicSdkSurface.Read(path);
        using var source = typeof(SdkSurfaceTests).Assembly.GetManifestResourceStream("Contracts.SdkPublicApi.json")!;
        using var descriptor = JsonDocument.Parse(source);
        using var observed = JsonDocument.Parse(before);
        Assert.True(JsonElement.DeepEquals(descriptor.RootElement, observed.RootElement));
        Assert.DoesNotContain(observed.RootElement.GetProperty("declarations").EnumerateArray(),
            item => item.GetProperty("Name").GetString() is "NexusPipeline.Extensibility.Protocol.ExecutionPlan" or
                "NexusPipeline.Extensibility.Protocol.ExecutionJournal" or "NexusPipeline.Extensibility.Protocol.FileCleanupPlan" or
                "NexusPipeline.Extensibility.Protocol.WriterJobClosure");
        var changed = EditSelectedName(original, publicChange);
        using var input = new MemoryStream(changed, writable: false);
        var after = PublicSdkSurface.Read(input);
        if (publicChange) Assert.NotEqual(before, after);
        else Assert.Equal(before, after);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private static byte[] EditSelectedName(byte[] original, bool publicChange)
    {
        using var input = new MemoryStream(original, writable: false);
        using var pe = new PEReader(input);
        var metadata = pe.GetMetadataReader();
        var owner = metadata.TypeDefinitions.Single(handle =>
        {
            var type = metadata.GetTypeDefinition(handle);
            return metadata.GetString(type.Name) == (publicChange ? "ExtensionActivationContext" : "ExecutionProviderChannel");
        });
        var typeDefinition = metadata.GetTypeDefinition(owner);
        var name = publicChange ? "PackageAssets" : "_recordedBytes";
        StringHandle selected = publicChange
            ? metadata.GetPropertyDefinition(typeDefinition.GetProperties().Single(handle =>
                metadata.GetString(metadata.GetPropertyDefinition(handle).Name) == name)).Name
            : metadata.GetFieldDefinition(typeDefinition.GetFields().Single(handle =>
                metadata.GetString(metadata.GetFieldDefinition(handle).Name) == name)).Name;
        var offset = checked(pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.String) +
            MetadataTokens.GetHeapOffset(selected));
        var expected = Encoding.UTF8.GetBytes(name);
        Assert.Equal(expected, original.AsSpan(offset, expected.Length).ToArray());
        Assert.Equal((byte)0, original[offset + expected.Length]);
        var changed = original.ToArray();
        changed[offset + expected.Length - 1] = (byte)'Z';
        Assert.Equal(original.AsSpan(0, offset).ToArray(), changed.AsSpan(0, offset).ToArray());
        Assert.Equal(original.AsSpan(offset + expected.Length).ToArray(), changed.AsSpan(offset + expected.Length).ToArray());
        return changed;
    }
}
