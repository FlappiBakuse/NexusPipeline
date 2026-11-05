using NexusPipeline.Core.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionCompilationBinding
{
    internal static CompiledProjectSnapshot? Require(ExecutionPlan total, ExecutionPlanStep step, string scriptRoot,
        ExtensionManifest manifest, ImmutableBlobStore blobs, ContractReader contracts, bool verifyContents)
    {
        var adapter = step.Adapter ?? throw Invalid(); var declaration = manifest.Specialized?.Compiler;
        if (declaration is null)
        {
            if (adapter.Compiled is not null || adapter.CompiledProvider is not null || adapter.CompiledResources is not null) throw Invalid();
            return null;
        }
        var reference = adapter.Compiled ?? throw Invalid();
        var snapshot = CompiledProjectCodec.Read(blobs.Instance, blobs, new(reference.RelativePath, reference.SizeBytes, reference.Sha256), verifyContents);
        var source = snapshot.Value; var consumer = source.Consumer; var provider = source.Provider;
        if (!source.CanExecute) throw new IOException("execution_compilation_readonly");
        if (step.Purpose == "initialization" && (source.InitializationOccurrences.IsEmpty ||
            !step.SelectedTaskIds.SequenceEqual(source.InitializationOccurrences) ||
            !step.Tasks.Select(task => task.TaskId).SequenceEqual(source.InitializationOccurrences))) throw Invalid();
        if (source.Choice is null || source.BindingId.ToString("D") != step.BindingId || source.RootId.ToString("D") != scriptRoot ||
            source.BindingRevision != Revision(total, "binding", step.BindingId!) || source.ScriptRevision != Revision(total, "script", step.ScriptId) ||
            source.RootRevision != Revision(total, "root", scriptRoot) || consumer.Installed.ExtensionId != step.ExtensionId ||
            consumer.Installed.Revision != adapter.InstalledRevision || consumer.Installed.JournalId.ToString("D") != adapter.InstalledJournalId ||
            consumer.Installed.Footprint.RelativePath != adapter.InstalledFootprint.RelativePath ||
            consumer.Installed.Footprint.SizeBytes != adapter.InstalledFootprint.SizeBytes || consumer.Installed.Footprint.Sha256 != adapter.InstalledFootprint.Sha256 ||
            consumer.Manifest.Sha256 != adapter.Manifest.Sha256 || consumer.Installed.Package.Kind is not (ExtensionPackageKind.DataSpecialized or ExtensionPackageKind.ManagedCode) ||
            source.CompilerId != declaration.CompilerId || provider.Installed.ExtensionId != declaration.ExtensionId ||
            adapter.CompiledProvider != declaration.ExtensionId || provider.Installed.Package.Kind != ExtensionPackageKind.ManagedCode) throw Invalid();
        var root = total.RootBindings.SingleOrDefault(item => item.RootId == scriptRoot) ?? throw Invalid();
        if (root.Identity.VolumeSerial != source.RootIdentity.VolumeSerial || root.Identity.FileId != source.RootIdentity.FileId ||
            !source.ReadSet.All(item => item.Address.BindingId == source.BindingId && item.ExpectedBindingRevision == source.BindingRevision) ||
            adapter.CompiledResources is null || !adapter.CompiledResources.SequenceEqual(source.ReadSet.Select(item => item.Address.ResourceId)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))) throw Invalid();
        foreach (var resource in adapter.Resources)
            if (!source.ReadSet.Any(item => item.Address.ResourceId == resource.ResourceId && item.ExpectedRevision == resource.Revision &&
                item.RelativePath == resource.RelativePath && item.SourceSha256 == resource.Sha256)) throw Invalid();
        using var input = blobs.OpenVerified(provider.Manifest);
        if (input.Length is < 1 or > StrictJson.MaximumBytes) throw Invalid();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var providerManifest = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, declaration.ExtensionId);
        if (!providerManifest.Capabilities.Contains("source.compile", StringComparer.Ordinal) ||
            providerManifest.Managed?.Compilers?.Contains(source.CompilerId, StringComparer.Ordinal) != true ||
            providerManifest.Permissions.FileAccess?.Contains("selected-script-root", StringComparer.Ordinal) != true) throw Invalid();
        RequireLock(total, consumer, manifest); RequireLock(total, provider, providerManifest);
        return snapshot;
    }

    internal static IEnumerable<BlobReference> References(ExecutionPlan plan, ImmutableBlobStore blobs)
        => plan.Steps.Where(step => step.Adapter?.Compiled is not null).SelectMany(step =>
        {
            var reference = step.Adapter!.Compiled!;
            return CompiledProjectCodec.Read(blobs.Instance, blobs, new(reference.RelativePath, reference.SizeBytes, reference.Sha256),
                verifyContents: false).Closure;
        }).Distinct();

    private static long Revision(ExecutionPlan plan, string kind, string id)
        => plan.SourceRevisions.SingleOrDefault(item => item.EntityKind == kind && item.EntityId == id)?.Revision ?? throw Invalid();
    private static void RequireLock(ExecutionPlan total, CompiledInstallation source, ExtensionManifest manifest)
    {
        var locked = total.ExtensionLocks.SingleOrDefault(item => item.ExtensionId == source.Installed.ExtensionId) ?? throw Invalid();
        if (locked.PackageVersion != source.Installed.Package.PackageVersion || locked.PackageSha256 != source.Installed.Package.ArchiveSha256 ||
            locked.ManifestSha256 != source.Manifest.Sha256 || source.Manifest.Sha256 != source.Installed.Package.ManifestSha256 ||
            manifest.Kind != (source.Installed.Package.Kind == ExtensionPackageKind.ManagedCode ? "managed-code" : "data-specialized") ||
            locked.SdkVersion != manifest.SdkVersion || locked.ContractFingerprint != manifest.ContractFingerprint) throw Invalid();
    }
    private static IOException Invalid() => new("execution_compilation_unbound");
}
