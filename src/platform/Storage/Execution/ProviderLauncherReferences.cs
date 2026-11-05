using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ProviderLauncherReferences
{
    [SupportedOSPlatform("windows")]
    internal static ImmutableArray<ResourceClaim> ReadLive(ExecutionPlanStep step,
        ExecutionProviderRegistry.PreparedExecutionProvider prepared) => prepared.Use(package =>
    {
        if (step.Provider?.Launcher is null || package.Declaration.Launcher is null ||
            step.Provider.ExtensionId != prepared.ExtensionId || step.Provider.ActivationId != prepared.ActivationId.ToString("D"))
            throw new IOException("execution_provider_launcher_authority_unbound");
        var claims = PhysicalResourceAdmission.Read(package.Root, ResourceClaimMode.Reference, includeDescendants: true)
            .Concat(PhysicalResourceAdmission.Read(package.ArchivePath, ResourceClaimMode.Reference, includeDescendants: false))
            .Append(new ResourceClaim("extension:" + prepared.ExtensionId, ResourceClaimMode.Reference));
        return PhysicalResourceAdmission.Merge(claims);
    });

    internal static void RequireSubset(ExecutionPlan total, IReadOnlyList<ResourceClaim> references, string extensionId)
    {
        var frozen = ExecutionPlanResources.Claims(total);
        if (references.Count is < 1 or > 4096 ||
            references.Any(claim => claim is null || claim.Mode != ResourceClaimMode.Reference ||
                !frozen.Contains(claim)) ||
            references.Select(claim => claim.ResourceKey).Distinct(StringComparer.Ordinal).Count() != references.Count ||
            !references.Contains(new ResourceClaim("extension:" + extensionId, ResourceClaimMode.Reference)))
            throw new IOException("execution_provider_launcher_reference_unavailable");
    }

    internal static void RequireCanRetain(ExecutionPlanStep step, IReadOnlyDictionary<string, ResourceClaimMode> physical)
    {
        if (step.Provider?.Launcher is null) return;
        foreach (var claim in PhysicalResourceAdmission.Read(step.Provider.PackageRoot, ResourceClaimMode.Reference, includeDescendants: true)
            .Concat(PhysicalResourceAdmission.Read(step.Provider.PackageArchive.Path, ResourceClaimMode.Reference, includeDescendants: false)))
            if (!physical.TryGetValue(claim.ResourceKey, out var mode) || mode != ResourceClaimMode.Reference)
                throw new IOException("execution_provider_launcher_reference_unavailable");
    }
}
