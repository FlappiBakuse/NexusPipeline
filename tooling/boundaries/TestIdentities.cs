using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class TestIdentities
{
    internal static string Read(string directory, string[]? selected = null)
    {
        var result = new List<object>();
        foreach (var path in Directory.GetFiles(Path.GetFullPath(directory), "NexusPipeline.*Tests.dll", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) != Path.GetFileNameWithoutExtension(path)) continue;
            if (selected is not null && !selected.Contains(Path.GetFileNameWithoutExtension(path))) continue;
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = metadata.GetMethodDefinition(methodHandle);
                    if (!method.GetCustomAttributes().Any(attribute => IsTest(metadata, attribute))) continue;
                    var name = metadata.GetString(method.Name);
                    var owner = TypeName(metadata, handle);
                    result.Add(new { assembly = Path.GetFileNameWithoutExtension(path), owner, name, identity = owner + "." + name });
                }
            }
        }
        if (result.Count == 0) throw new InvalidOperationException("No compiled test identities");
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool IsTest(MetadataReader metadata, CustomAttributeHandle handle)
    {
        var constructor = metadata.GetCustomAttribute(handle).Constructor;
        if (constructor.Kind != HandleKind.MemberReference) return false;
        var parent = metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent;
        if (parent.Kind != HandleKind.TypeReference) return false;
        var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
        return metadata.GetString(type.Namespace) == "Xunit" && metadata.GetString(type.Name) is "FactAttribute" or "TheoryAttribute";
    }

    private static string TypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var declaring = type.GetDeclaringType();
        return declaring.IsNil ? metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name)
            : TypeName(metadata, declaring) + "+" + metadata.GetString(type.Name);
    }
}
