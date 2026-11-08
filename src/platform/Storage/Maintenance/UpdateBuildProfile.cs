using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NexusPipeline.Platform.Storage.Archives;
using NexusPipeline.Extensibility;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal static class UpdateBuildProfile
{
    internal static void RequireProduction(UpdatePayloadManifest manifest, ArchivedFileTree contents, ImmutableBlobStore blobs)
        => Require(manifest, contents, blobs, "production");

    internal static void Require(UpdatePayloadManifest manifest, ArchivedFileTree contents, ImmutableBlobStore blobs, string profile)
    {
        if (manifest.Profile != profile) throw Invalid();
        foreach (var (entry, assembly) in new[] { (manifest.HostEntry, "NexusPipeline.Host.dll"),
            (manifest.MaintenanceEntry, "NexusPipeline.Maintenance.dll") })
        {
            var file = manifest.Files.SingleOrDefault(file => file.RelativePath == entry) ?? throw Invalid();
            var reference = contents.Contents.SingleOrDefault(blob => blob.Sha256 == file.Sha256 && blob.SizeBytes == file.SizeBytes) ?? throw Invalid();
            using var input = blobs.OpenVerified(reference);
            RequireBundle(input, assembly, profile);
        }
        if (manifest.Files.SingleOrDefault(file => file.RelativePath == manifest.LauncherEntry) is { } launcher)
        {
            var reference = contents.Contents.SingleOrDefault(blob => blob.Sha256 == launcher.Sha256 && blob.SizeBytes == launcher.SizeBytes)
                ?? throw Invalid();
            using var input = blobs.OpenVerified(reference);
            RequireBundle(input, "NexusPipeline.Launcher.dll", profile);
        }
    }

    internal static void RequireBundle(Stream input, string assembly, string profile)
    {
        try
        {
            using var managed = new MemoryStream(DotnetBundle.ReadAssembly(input, assembly), writable: false);
            Require(managed, profile);
            using var sdk = new MemoryStream(DotnetBundle.ReadAssembly(input, "NexusPipeline.Extensibility.dll"), writable: false);
            using var image = new PEReader(sdk);
            if (!image.HasMetadata) throw Invalid();
            var metadata = image.GetMetadataReader(); var identity = metadata.GetAssemblyDefinition();
            if (metadata.GetString(identity.Name) != "NexusPipeline.Extensibility" || identity.Version.ToString(3) != SdkIdentity.Version)
                throw Invalid();
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new IOException("update_payload_profile_invalid", error); }
    }

    internal static void RequireProduction(Stream input)
        => Require(input, "production");

    internal static void Require(Stream input, string requiredProfile)
    {
        if (requiredProfile is not ("production" or "test")) throw Invalid();
        try
        {
            using var image = new PEReader(input, PEStreamOptions.LeaveOpen);
            if (!image.HasMetadata) throw Invalid();
            var reader = image.GetMetadataReader(); var matches = 0;
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                if (reader.GetString(type.Namespace) != "System.Reflection" || reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
                var value = reader.GetBlobReader(attribute.Value);
                if (value.Length > 4096 || value.ReadUInt16() != 1) throw Invalid();
                var key = value.ReadSerializedString(); var profile = value.ReadSerializedString();
                if (key != "Nexus.BuildProfile") continue;
                if (profile != requiredProfile || ++matches != 1 || value.RemainingBytes != 2 || value.ReadUInt16() != 0) throw Invalid();
            }
            if (matches != 1) throw Invalid();
        }
        catch (Exception error) when (error is BadImageFormatException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new IOException("update_payload_profile_invalid", error); }
    }

    private static IOException Invalid() => new("update_payload_profile_invalid");
}
