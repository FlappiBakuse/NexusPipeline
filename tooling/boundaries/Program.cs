using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

if (args.Length == 3 && args[0] == "--source-names")
    return SourceNames.Verify(args[1], args[2]);
if (args.Length == 2 && args[0] == "--source-boundaries")
    return SourceBoundaries.Verify(args[1]);

if (args.Length == 4 && args[0] == "--sdk-surface" && args[2] is "--write" or "--verify")
{
    try
    {
        if (args[2] == "--verify") PublicSdkSurface.Verify(args[1], args[3]);
        else File.WriteAllText(Path.GetFullPath(args[3]), PublicSdkSurface.Read(args[1]), new System.Text.UTF8Encoding(false));
        return 0;
    }
    catch (Exception error) when (error is IOException or BadImageFormatException or JsonException or InvalidOperationException)
    { Console.Error.WriteLine(error.GetType().Name + ": sdk_public_api_unverified"); return 1; }
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: NexusPipeline.Boundaries <build-bin-root>");
    return 2;
}

var allowed = new Dictionary<string, string[]>
{
    ["NexusPipeline.Core"] = [],
    ["NexusPipeline.Extensibility"] = [],
    ["NexusPipeline.Platform"] = ["NexusPipeline.Core", "NexusPipeline.Extensibility"],
    ["NexusPipeline.Host"] = ["NexusPipeline.Core", "NexusPipeline.Platform", "NexusPipeline.Extensibility"],
    ["NexusPipeline.Launcher"] = [],
    ["NexusPipeline.Maintenance"] = ["NexusPipeline.Core", "NexusPipeline.Platform"],
    ["NexusPipeline.Installer"] = ["NexusPipeline.Core", "NexusPipeline.Platform"]
};
var results = new List<object>();
var errors = new List<string>();
foreach (var (name, permitted) in allowed)
{
    var matches = Directory.GetFiles(Path.GetFullPath(args[0]), name + ".dll", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) == name)
        .ToArray();
    if (matches.Length != 1)
    {
        errors.Add($"{name}: expected one own compiled assembly, found {matches.Length}");
        continue;
    }

    using var stream = File.OpenRead(matches[0]);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var references = metadata.AssemblyReferences
        .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
    foreach (var dependency in references)
    {
        if (dependency.StartsWith("NexusPipeline.", StringComparison.Ordinal) && !permitted.Contains(dependency))
            errors.Add($"{name} -> {dependency} is forbidden");
        if (name is "NexusPipeline.Core" or "NexusPipeline.Extensibility" &&
            dependency is not ("System.Runtime" or "System.Collections" or "System.Threading" or
                "System.Text.Json" or "System.Memory" or "System.Linq" or "System.Threading.Tasks") &&
            !(name == "NexusPipeline.Core" && dependency is "System.Collections.Immutable" or "System.Security.Cryptography" or "System.Threading.Channels" or "System.Text.RegularExpressions") &&
            !(name == "NexusPipeline.Extensibility" && dependency is "System.IO.Pipes" or
                "System.Security.Cryptography" or "System.Text.Encoding.Extensions"))
            errors.Add($"{name}: unreviewed framework dependency {dependency}");
    }
    if (name == "NexusPipeline.Host")
        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            var scope = type.ResolutionScope;
            while (scope.Kind == System.Reflection.Metadata.HandleKind.TypeReference)
            {
                type = metadata.GetTypeReference((TypeReferenceHandle)scope);
                scope = type.ResolutionScope;
            }
            var typeNamespace = metadata.GetString(type.Namespace);
            var typeName = metadata.GetString(type.Name);
            if (scope.Kind == System.Reflection.Metadata.HandleKind.AssemblyReference &&
                metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) == "NexusPipeline.Extensibility" &&
                typeNamespace != "NexusPipeline.Extensibility.Protocol" &&
                !(typeNamespace == "NexusPipeline.Extensibility.State" && typeName == "DocumentCommit"))
                errors.Add($"{name}: SDK runtime type {typeNamespace}.{typeName} is forbidden");
        }
    if (name == "NexusPipeline.Extensibility")
    {
        try { PublicSdkSurface.Verify(matches[0], Path.GetFullPath("contracts/sdk-public-api.json")); }
        catch (Exception error) when (error is IOException or BadImageFormatException or JsonException or InvalidOperationException)
        { errors.Add("NexusPipeline.Extensibility: public API descriptor differs from the compiled assembly"); }
    }
    results.Add(new { assembly = name, references });
}

Console.WriteLine(JsonSerializer.Serialize(new { results, errors }, new JsonSerializerOptions { WriteIndented = true }));
return errors.Count == 0 ? 0 : 1;
