using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class AdapterPlanCapture(ExtensionActivator activator, SecretAccessPolicy secrets,
    ProjectCompilationService compilation, ImmutableBlobStore blobs, ContractReader contracts)
{
    internal ValueTask<AdapterPlanSource> CaptureAsync(ResolvedAdapterSelection source, CancellationToken cancellation, bool initialize = false)
        => activator.RequirePublishedRuntime(source.Selection.ExtensionId).InvokeAdapterAsync(source.Root, source.RequireCurrent,
            async (executor, requireCurrent) =>
            {
                void Current() { source.RequireCurrent(); requireCurrent(); }
                using var grant = secrets.EnterSelectedBinding(source.Root, source.Selection.BindingId, source.Selection.ExtensionId, Current);
                if (!executor.UsesCompilation)
                {
                    if (initialize) throw new IOException("execution_initialization_unavailable");
                    return Capture(source, executor, null, Current, source.RequireLive, cancellation, false);
                }
                return await compilation.UseSavedAsync(source.Selection.BindingId, source.Selection.ExpectedBindingRevision,
                    source.Selection.ExtensionId, source.RequireLive, (snapshot, current, live) =>
                    {
                        if (!snapshot.Value.CanExecute) throw new IOException("execution_compilation_readonly");
                        var result = Capture(source, executor, snapshot, current, live, cancellation, initialize);
                        Current(); return ValueTask.FromResult(result);
                    }, cancellation, source.Root).ConfigureAwait(false);
            });

    private AdapterPlanSource Capture(ResolvedAdapterSelection source, TaskAdapterExecutor executor, CompiledProjectSnapshot? snapshot,
        Action current, Action live, CancellationToken cancellation, bool initialize)
    {
        var identity = new TaskMessageIdentity(source.Selection.ExtensionId, source.Selection.OperationId, "discover", null, null);
        using var compiled = snapshot is null ? null : new CompiledAdapterInput(blobs, snapshot, live);
        if (snapshot is not null) source.BindCompilation(snapshot.Document.Sha256);
        var response = initialize ? null : executor.Execute(JsonSerializer.SerializeToUtf8Bytes(new TaskAdapter
        {
            Format = "nexus.task.adapter", Version = 1, Direction = "request", Phase = "discover", ExtensionId = identity.ExtensionId,
            OperationId = identity.OperationId.ToString("D"), Resources = source.Resources, Compiled = compiled?.Wire
        }), identity, cancellation, compiled, source.ReadSources(executor, cancellation));
        var original = executor.ReadInputs();
        var manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(original.Manifest, identity.ExtensionId);
        var manifestHash = Digest(original.Manifest); var scriptHash = Digest(original.Script);
        if (manifestHash != source.Installed.Package.ManifestSha256) throw new IOException("execution_adapter_manifest_unbound");
        var descriptor = new ExecutionAdapter
        {
            InstalledRevision = source.Installed.Revision, InstalledJournalId = source.Installed.JournalId.ToString("D"),
            InstalledFootprint = new() { RelativePath = source.Installed.Footprint.RelativePath,
                SizeBytes = source.Installed.Footprint.SizeBytes, Sha256 = source.Installed.Footprint.Sha256 },
            Manifest = new() { RelativePath = $"blobs/content/{manifestHash}.bin", SizeBytes = original.Manifest.LongLength, Sha256 = manifestHash },
            Script = new() { RelativePath = $"blobs/content/{scriptHash}.bin", SizeBytes = original.Script.LongLength, Sha256 = scriptHash },
            Resources = source.Stamp.Resources.Select(resource => new ExecutionAdapterResource
                { ResourceId = resource.ResourceId, RelativePath = resource.RelativePath, Revision = resource.Revision, Sha256 = resource.Sha256 }).ToArray(),
            Compiled = snapshot is null ? null : new() { RelativePath = snapshot.Document.RelativePath, SizeBytes = snapshot.Document.SizeBytes, Sha256 = snapshot.Document.Sha256 },
            SourceFiles = source.FreezeSources(cancellation),
            CompiledProvider = snapshot?.Value.Provider.Installed.ExtensionId,
            CompiledResources = snapshot?.Value.ReadSet.Select(item => item.Address.ResourceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        };
        var tasks = initialize ? ImmutableArray<AdapterTask>.Empty :
            AdapterTaskPolicy.Freeze(response!.Tasks!.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray());
        if (initialize)
        {
            if (snapshot is null || snapshot.Value.InitializationOccurrences.IsDefaultOrEmpty || manifest.Specialized?.ExecutionProvider is null)
                throw new IOException("execution_initialization_unavailable");
            tasks = AdapterTaskPolicy.Freeze(snapshot.Value.InitializationOccurrences.Select(id =>
                new AdapterTask(id, id, true, true, [], false, AdapterWorkflowRole.Technical)).ToImmutableArray());
        }
        var diagnostics = (response?.Diagnostics ?? []).Select(item => new AdapterDiagnostic(item.Code, item.ResourceId, item.Severity switch
        { "info" => AdapterDiagnosticSeverity.Information, "warning" => AdapterDiagnosticSeverity.Warning,
            "blocking" => AdapterDiagnosticSeverity.Blocking, _ => throw new IOException("adapter_diagnostic_severity_invalid") })).ToImmutableArray();
        var result = new AdapterPlanSource(source.Configurations, new(source.Stamp, tasks, diagnostics), descriptor,
            new() { ExtensionId = identity.ExtensionId, PackageVersion = source.Installed.Package.PackageVersion, SdkVersion = manifest.SdkVersion,
                ContractFingerprint = manifest.ContractFingerprint, ManifestSha256 = manifestHash, PackageSha256 = source.Installed.Package.ArchiveSha256 },
            new Dictionary<string, byte[]> { [manifestHash] = original.Manifest, [scriptHash] = original.Script })
            { ExecutionProvider = manifest.Specialized?.ExecutionProvider, ExclusiveProgram = manifest.Specialized?.ExclusiveProgram == true };
        if (snapshot is not null)
        {
            var provider = snapshot.Value.Provider;
            using var input = blobs.OpenVerified(provider.Manifest); var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            var providerManifest = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, provider.Installed.ExtensionId);
            result = result with { CompilerLocks = [new() { ExtensionId = provider.Installed.ExtensionId, PackageVersion = provider.Installed.Package.PackageVersion,
                SdkVersion = providerManifest.SdkVersion, ContractFingerprint = providerManifest.ContractFingerprint,
                ManifestSha256 = provider.Manifest.Sha256, PackageSha256 = provider.Installed.Package.ArchiveSha256 }] };
        }
        current(); return result;
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
