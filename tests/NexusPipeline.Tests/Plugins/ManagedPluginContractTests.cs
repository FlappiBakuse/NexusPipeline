using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Plugin.Abstractions;
using Xunit;

namespace NexusPipeline.Tests.Plugins;

public sealed class ManagedPluginContractTests
{
    [Fact]
    public void EntryMetadataRejectsOldFrameworkAndSdkWithoutChangingBytes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nxp-entry-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var required = typeof(INexusPlugin).Assembly.GetName().Version!;
            foreach (var (framework, sdk, accepted) in new (string?, Version?, bool)[]
            {
                (ManagedPluginContract.TargetFramework, required, true),
                (".NETCoreApp,Version=v8.0", required, false),
                (".NETCoreApp,Version=v9.0", required, false),
                (null, required, false),
                (ManagedPluginContract.TargetFramework, new Version(1, 0, 0, 0), false),
                (ManagedPluginContract.TargetFramework, null, false),
            })
            {
                string entry = Path.Combine(directory, "Entry.dll");
                byte[] bytes = Entry(framework, sdk);
                File.WriteAllBytes(entry, bytes);
                if (accepted) ManagedPluginContract.ValidateEntry(entry, "Fixture");
                else Assert.Throws<InvalidDataException>(() => ManagedPluginContract.ValidateEntry(entry, "Fixture"));
                Assert.Equal(bytes, File.ReadAllBytes(entry));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static byte[] Entry(string? framework, Version? sdk)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("Entry.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        var assembly = metadata.AddAssembly(metadata.GetOrAddString("Entry"), new Version(1, 0), default, default,
            (AssemblyFlags)0, AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        if (sdk is not null)
            metadata.AddAssemblyReference(metadata.GetOrAddString(typeof(INexusPlugin).Assembly.GetName().Name!), sdk,
                default, default, (AssemblyFlags)0, default);
        if (framework is not null)
        {
            var system = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(10, 0),
                default, default, (AssemblyFlags)0, default);
            var attribute = metadata.AddTypeReference(system, metadata.GetOrAddString("System.Runtime.Versioning"),
                metadata.GetOrAddString("TargetFrameworkAttribute"));
            var constructor = metadata.AddMemberReference(attribute, metadata.GetOrAddString(".ctor"),
                metadata.GetOrAddBlob(new byte[] { 0x20, 0x01, 0x01, 0x0e }));
            var value = new BlobBuilder(); value.WriteUInt16(1); value.WriteSerializedString(framework); value.WriteUInt16(0);
            metadata.AddCustomAttribute(assembly, constructor, metadata.GetOrAddBlob(value));
        }
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder(), flags: CorFlags.ILOnly);
        var output = new BlobBuilder(); pe.Serialize(output); return output.ToArray();
    }
}
