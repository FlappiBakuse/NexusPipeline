using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionProviderBinding
{
    internal static IReadOnlyList<BlobReference> Require(ExecutionPlan total, ImmutableBlobStore blobs,
        ContractReader contracts, IReadOnlyDictionary<string, byte[]>? previewManifests = null)
    {
        var references = new List<BlobReference>();
        foreach (var step in total.Steps)
        {
            if (step.EngineKind == "process")
            {
                if (step.Provider is not null) throw new IOException("execution_provider_unbound");
                continue;
            }
            if (step.EngineKind != "extensionWorker" || step.Provider is not { } provider ||
                provider.EntryId != step.EntryId || string.IsNullOrEmpty(step.ExtensionId))
                throw new IOException("execution_provider_unbound");
            ExecutionPlanSources.RequireCanonicalId(provider.ActivationId);
            var reference = Reference(provider.Manifest);
            byte[] bytes;
            if (previewManifests is not null && previewManifests.TryGetValue(provider.Manifest.Sha256, out var preview))
                bytes = preview.ToArray();
            else
            {
                using var input = blobs.OpenVerified(reference);
                bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            }
            if (bytes.LongLength != reference.SizeBytes ||
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != reference.Sha256)
                throw new IOException("execution_provider_manifest_unbound");
            var manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, provider.ExtensionId);
            var declaration = manifest.Execution ?? throw new IOException("execution_provider_descriptor_missing");
            var locked = total.ExtensionLocks.SingleOrDefault(item => item.ExtensionId == provider.ExtensionId)
                ?? throw new IOException("execution_provider_lock_unbound");
            if (locked.ManifestSha256 != reference.Sha256 || locked.PackageSha256 != provider.PackageArchive.Sha256 ||
                locked.PackageVersion != manifest.PackageVersion || locked.SdkVersion != manifest.SdkVersion ||
                locked.ContractFingerprint != manifest.ContractFingerprint || provider.Verification != "controlled-local-package" ||
                provider.Protocol != ExecutionProviderPolicy.Protocol || declaration.Protocol != provider.Protocol ||
                provider.GracefulStop != declaration.GracefulStop || provider.TargetControl != declaration.TargetControl ||
                provider.EntryId != declaration.EntryId || !manifest.Capabilities.Contains("execution.drive", StringComparer.Ordinal) ||
                manifest.Permissions.ProcessAccess?.Contains("owned-worker", StringComparer.Ordinal) != true ||
                manifest.Permissions.ProcessAccess?.Contains("selected-upstream", StringComparer.Ordinal) != true)
                throw new IOException("execution_provider_lock_unbound");
            if (step.Adapter is null && provider.ExtensionId != step.ExtensionId ||
                step.Adapter is not null && declaration.ConfigurationResources?.Count > 0)
                throw new IOException("execution_provider_consumer_unbound");
            if (provider.PackageRoot != PhysicalPathPolicy.NormalizeSyntax(provider.PackageRoot) ||
                Image(provider.PackageRoot, declaration.WorkerEntry) != provider.Worker.Path ||
                step.ExecutablePath != provider.Worker.Path || step.ImageSizeBytes != provider.Worker.SizeBytes ||
                step.ImageSha256 != provider.Worker.Sha256 || step.ImageIdentity != provider.Worker.Identity ||
                step.ParentIdentity != provider.Worker.ParentIdentity || !step.Arguments.SequenceEqual(declaration.Arguments))
                throw new IOException("execution_provider_worker_unbound");
            if (!System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(declaration.Retry is null ? null : declaration.Retry with
                    { Outcomes = declaration.Retry.Outcomes.Order(StringComparer.Ordinal).ToArray() }),
                System.Text.Json.JsonSerializer.SerializeToElement(provider.Retry)))
                throw new IOException("execution_provider_retry_authority_unbound");
            RequireImage(provider.Worker, provider.PackageRoot);
            ProviderControlInputs.RequireFits(step);
            if (step.Adapter is null) ProviderConfigurationPolicy.RequireFrozenSlots(total, step, declaration.ConfigurationResources);
            if ((declaration.Launcher is null) != (provider.Launcher is null))
                throw new IOException("execution_provider_launcher_unbound");
            if (provider.Launcher is { } launcher && declaration.Launcher is { } declared)
            {
                if (launcher.WritesManagedConfiguration || declared.WritesManagedConfiguration ||
                    launcher.TerminalProtocol != ExecutionProviderPolicy.LauncherTerminalProtocol ||
                    declared.TerminalProtocol != launcher.TerminalProtocol || !launcher.Arguments.SequenceEqual(declared.Arguments) ||
                    Image(provider.PackageRoot, declared.Entry) != launcher.Image.Path)
                    throw new IOException("execution_provider_launcher_unbound");
                RequireImage(launcher.Image, provider.PackageRoot);
            }
            references.Add(reference);
        }
        return references.Distinct().ToArray();
    }

    internal static IEnumerable<BlobReference> References(ExecutionPlan plan) =>
        plan.Steps.Where(step => step.Provider is not null).Select(step => Reference(step.Provider!.Manifest)).Distinct();

    private static BlobReference Reference(ExecutionPlanPlanReference reference)
    {
        if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json" || reference.SizeBytes is < 1 or > StrictJson.MaximumBytes)
            throw new IOException("execution_provider_manifest_reference_invalid");
        return new(reference.RelativePath, reference.SizeBytes, reference.Sha256);
    }

    private static string Image(string root, string relative)
    {
        PhysicalPathPolicy.ValidateRelativePath(relative);
        return PhysicalPathPolicy.NormalizeSyntax(Path.Combine(root, relative));
    }

    private static void RequireImage(ExecutionPlanInterpreterImage image, string root)
    {
        var relative = Path.GetRelativePath(root, image.Path).Replace('\\', '/');
        PhysicalPathPolicy.ValidateRelativePath(relative);
        if (Image(root, relative) != image.Path ||
            image.RuntimeFiles.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != image.RuntimeFiles.Count)
            throw new IOException("execution_provider_package_scope_unbound");
        foreach (var file in image.RuntimeFiles)
        {
            PhysicalPathPolicy.ValidateRelativePath(file.RelativePath);
            PhysicalPathPolicy.ValidateRelativePath(Path.GetRelativePath(root,
                Path.Combine(Path.GetDirectoryName(image.Path)!, file.RelativePath)).Replace('\\', '/'));
        }
    }
}
