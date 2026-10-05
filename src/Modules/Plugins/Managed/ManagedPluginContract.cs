using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Modules.Plugins.Managed;

internal static class ManagedPluginContract
{
    internal const string TargetFramework = ".NETCoreApp,Version=v10.0";

    internal static string Validate(string directory, PluginManifest manifest)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        string relative = manifest.EntryAssembly.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"{manifest.ArtifactName}: entryAssembly path invalid");
        string entry = Path.GetFullPath(Path.Combine(root, relative));
        for (string? path = entry; path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"{manifest.ArtifactName}: linked entryAssembly rejected");
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        string sdkName = typeof(INexusPlugin).Assembly.GetName().Name!;
        if (Directory.EnumerateFiles(root).Any(file => Path.GetFileName(file).Equals(sdkName + ".dll", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(file).Equals(sdkName + ".deps.json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{manifest.ArtifactName}: SDK copy in plugin root rejected; Host supplies {sdkName}");
        ValidateEntry(entry, manifest.ArtifactName);
        return entry;
    }

    internal static void ValidateEntry(string entry, string artifact)
    {
        using var stream = File.OpenRead(entry);
        using var image = new PEReader(stream);
        if (!image.HasMetadata) throw new InvalidDataException($"{artifact}: managed entry metadata missing");
        MetadataReader metadata = image.GetMetadataReader();
        if (!metadata.IsAssembly) throw new InvalidDataException($"{artifact}: entry is not an assembly");
        var frameworks = new List<string?>();
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(type.Namespace) != "System.Runtime.Versioning"
                || metadata.GetString(type.Name) != "TargetFrameworkAttribute") continue;
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) throw new InvalidDataException($"{artifact}: invalid target framework attribute");
            frameworks.Add(blob.ReadSerializedString());
        }
        if (frameworks.Count != 1 || frameworks[0] != TargetFramework)
            throw new InvalidDataException($"{artifact}: target framework {string.Join(",", frameworks)}; required {TargetFramework}");
        AssemblyName required = typeof(INexusPlugin).Assembly.GetName();
        var references = metadata.AssemblyReferences.Select(metadata.GetAssemblyReference)
            .Where(reference => metadata.GetString(reference.Name) == required.Name).ToArray();
        if (references.Length != 1 || references[0].Version != required.Version
            || metadata.GetString(references[0].Culture) != (required.CultureName ?? "")
            || !metadata.GetBlobBytes(references[0].PublicKeyOrToken).SequenceEqual(required.GetPublicKeyToken() ?? []))
            throw new InvalidDataException($"{artifact}: SDK reference identity incompatible; required {required.FullName}");
    }
}
