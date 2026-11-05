using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionWorkClaims
{
    internal static ImmutableArray<ResourceClaim> Read(BoundExtensionWork origin)
    {
        var source = origin.Producer.Source;
        var proof = origin.Proof;
        var claims = PhysicalResourceAdmission.Merge(source.Claims.Where(item =>
                item.Mode == ResourceClaimMode.Reference && item.ResourceKey.StartsWith("filesystem:", StringComparison.Ordinal))
            .Concat(new ResourceClaim[]
            {
                new("extension:" + source.ExtensionId, ResourceClaimMode.Mutation),
                new("blobs:content", ResourceClaimMode.Reference),
                new(PhysicalResourceAdmission.EntryKey(proof.ParentIdentity, Path.GetFileName(proof.WorkspacePath)), ResourceClaimMode.Mutation),
                new(ObjectKey(proof.WorkspaceIdentity), ResourceClaimMode.Mutation)
            }).Concat(proof.Nodes.Where(node => node.Kind == FileObjectKind.File)
                .Select(node => new ResourceClaim(ObjectKey(node.Identity), ResourceClaimMode.Mutation))));
        if (claims.Length > 4096) throw new IOException("extension_work_claim_limit");
        return claims;
    }

    internal static string ObjectKey(PhysicalObjectIdentity identity) =>
        "filesystem:object:" + identity.VolumeSerial + ":" + identity.FileId;
}
