using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class SdkSurfaceTests
{
    [Theory(DisplayName = "Public SDK Metadata Changes Compatibility While Private Names Do Not")]
    [InlineData(true)]
    [InlineData(false)]
    public void ChecksPublicMetadata(bool publicChange)
    {
        using var original = Assembly("PublicValue", "privateValue");
        using var changed = Assembly(publicChange ? "OtherValue" : "PublicValue",
            publicChange ? "privateValue" : "otherValue");
        var before = PublicSdkSurface.Read(original);
        var after = PublicSdkSurface.Read(changed);
        if (publicChange) Assert.NotEqual(before, after);
        else Assert.Equal(before, after);
    }

    private static MemoryStream Assembly(string publicName, string privateName)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("Fixture.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("NexusPipeline.Extensibility"), new Version(0, 1, 0, 0),
            default, default, (AssemblyFlags)0, AssemblyHashAlgorithm.None);
        var field = MetadataTokens.FieldDefinitionHandle(1);
        var method = MetadataTokens.MethodDefinitionHandle(1);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default, field, method);
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("Fixture"),
            metadata.GetOrAddString("Contract"), default, field, method);
        var signature = new BlobBuilder();
        new BlobEncoder(signature).FieldSignature().Int32();
        var encoded = metadata.GetOrAddBlob(signature);
        metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString(publicName), encoded);
        metadata.AddFieldDefinition(FieldAttributes.Private, metadata.GetOrAddString(privateName), encoded);
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder()).Serialize(image);
        return new MemoryStream(image.ToArray(), writable: false);
    }
}
