using System.Net;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class LocalExtensionInstaller(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources, RecoveryIndex recovery,
    ExtensionRuntimeLifecycle runtime)
{
    internal async Task InstallAllAsync(string directory, CancellationToken cancellation)
    {
        directory = PhysicalPathPolicy.NormalizeWithoutLinks(directory);
        if (!Directory.Exists(directory)) throw new IOException("extension_bundle_missing");
        var trust = runtime.ReleaseTrust ?? throw new IOException("extension_release_trust_unprovisioned");
        var catalog = new ExtensionCatalogVerifier(contracts, trust.Policy).Verify(
            Read(directory, "index.json"), Read(directory, "catalog.json"), Read(directory, "catalog.sig.json"), ExtensionStoreChannel.Stable);
        var ids = catalog.Entries.Select(item => item.ExtensionId).ToArray();
        var evidence = new ExtensionInstalledEvidence(instance, database, contracts, roots);
        var selected = ids.Select(id => (Entry: catalog.Entries.SingleOrDefault(item => item.ExtensionId == id)
                ?? throw new IOException("extension_bundle_entry_missing"), Installed: evidence.ReadCurrent(id)?.Metadata))
            .Where(item => item.Installed is null || !item.Installed.IsInstalled ||
                item.Installed.Package.ArchiveSha256 != item.Entry.ArtifactSha256 ||
                item.Installed.Package.ManifestSha256 != item.Entry.ManifestSha256).ToArray();
        if (selected.Length == 0 && runtime.Catalog!.ReadCached(catalog.Identity.Channel) is { } current &&
            current.Identity == catalog.Identity && current.PointerBytes().AsSpan().SequenceEqual(catalog.PointerBytes()) &&
            current.CatalogBytes().AsSpan().SequenceEqual(catalog.CatalogBytes()) &&
            current.SignatureBytes().AsSpan().SequenceEqual(catalog.SignatureBytes())) return;
        var paths = selected.ToDictionary(item => item.Entry.ArtifactUrl,
            item => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(directory,
                item.Entry.ArtifactName + "-" + item.Entry.PackageVersion + ".zip")), StringComparer.Ordinal);
        if (paths.Values.Any(path => !File.Exists(path))) throw new IOException("extension_bundle_package_missing");
        ExtensionProjectionPublication.PublishIfDirty(instance, database, lease, contracts, roots, resources, recovery);
        _ = runtime.Catalog!.Accept(Guid.NewGuid(), "local:bundled-installation", catalog);
        if (selected.Length == 0) return;
        using var transport = new StoreHttpTransport(contracts, trust.Policy, () => new HttpClient(new BundleHandler(paths)));
        var workflow = new ExtensionApplyWorkflow(instance, database, lease, contracts, roots, resources, recovery,
            runtime.Catalog, runtime.Activator, transport);
        foreach (var (entry, installed) in selected)
        {
            cancellation.ThrowIfCancellationRequested();
            var action = installed?.IsInstalled == true ? PayloadAction.Upgrade : PayloadAction.Install;
            var request = new ExtensionApplyRequest(action, entry.ExtensionId, installed?.Revision ?? 0,
                new HostSettingsStore(database).Read().Revision, catalog.Identity, entry);
            var result = await workflow.RunAsync(Guid.NewGuid(), "local:bundled-installation", request, _ => { }, cancellation);
            if (result.Result is not { IsInstalled: true }) throw new IOException("extension_bundle_installation_incomplete");
        }
    }

    private static byte[] Read(string directory, string name)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(directory, name));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > StrictJson.MaximumBytes) throw new IOException("extension_bundle_metadata_size_invalid");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }

    private sealed class BundleHandler(IReadOnlyDictionary<string, string> paths) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.RequestUri is null || !paths.TryGetValue(request.RequestUri.AbsoluteUri, out var path))
                throw new IOException("extension_bundle_artifact_unbound");
            path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var content = new StreamContent(stream);
            content.Headers.ContentLength = stream.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
    }
}
