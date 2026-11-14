using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
public sealed class InitialExtensionInstaller(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources, RecoveryIndex recovery,
    ExtensionRuntimeLifecycle runtime, IPayloadPersistenceObserver? observer = null)
{
    private const string Subject = "host:initial-extensions";
    private readonly InitialExtensionStateStore _state = new(instance, lease);
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);

    public async Task InitializeAsync(string source, CancellationToken cancellation)
    {
        lease.VerifyHeld(instance);
        using var inputs = resources.TryAcquire(Guid.NewGuid(), [new("blobs:content", ResourceClaimMode.Reference)]).Lease
            ?? throw new IOException("initial_extensions_resources_unavailable");
        var state = _state.Read();
        if (state is null)
        {
            source = PhysicalPathPolicy.NormalizeWithoutLinks(source);
            if (!Directory.Exists(source))
            {
                if (RequiresSource(source))
                    throw new IOException("initial_extensions_source_missing");
                return;
            }
            state = Capture(source);
        }
        if (state.Complete) { Cleanup(state); return; }
        var catalog = VerifySource(state);
        if (state.ExtensionIds.All(id => new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(id) is not null))
        {
            state = SetPreferences(state, enabled: true);
            Cleanup(_state.Save(state with { Complete = true, Request = null })); return;
        }
        _ = runtime.Catalog!.Accept(state.CatalogOperation, Subject, catalog);
        state = SetPreferences(state, enabled: false);
        foreach (var id in state.ExtensionIds)
        {
            cancellation.ThrowIfCancellationRequested();
            var current = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(id);
            if (current is not null) continue;
            var entry = catalog.Entries.Single(item => item.ExtensionId == id);
            if (state.Request?.ExtensionId != id)
                state = _state.Save(state with { OperationId = Guid.NewGuid(),
                    Request = new(PayloadAction.Install, id, 0, new HostSettingsStore(database).Read().Revision, catalog.Identity, entry) });
            using var transport = new StoreHttpTransport(contracts, runtime.ReleaseTrust!.Policy,
                () => new HttpClient(new InitialPackageHandler()));
            var workflow = new ExtensionApplyWorkflow(instance, database, lease, contracts, roots, resources, recovery,
                runtime.Catalog, runtime.Activator, transport, observer);
            var query = new ExtensionQueryService(instance, lease, database, contracts, roots);
            if (query.Read(state.OperationId, Subject) is { Outcome: ExtensionApplyOutcome.NotExecuted or ExtensionApplyOutcome.OriginalRestored })
                state = _state.Save(state with { OperationId = Guid.NewGuid(), Request = state.Request! with
                    { ExpectedSettingsRevision = new HostSettingsStore(database).Read().Revision } });
            _ = await workflow.RunAsync(state.OperationId, Subject, state.Request!, _ => { }, cancellation).ConfigureAwait(false);
            if (new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(id)?.Metadata.IsInstalled != true)
                throw new IOException("initial_extensions_installation_pending");
        }
        state = SetPreferences(state, enabled: true);
        state = _state.Save(state with { Complete = true, Request = null });
        Cleanup(state);
    }

    private InitialExtensionState Capture(string source)
    {
        var trust = runtime.ReleaseTrust ?? throw new IOException("extension_release_trust_unprovisioned");
        using var selection = StrictJson.Parse(Read(source, "selection.json", 16384));
        var root = selection.RootElement;
        if (root.EnumerateObject().Count() != 3 || root.GetProperty("format").GetString() != "nexus.extensions.initial-selection" ||
            root.GetProperty("version").GetInt32() != 1) throw InitialExtensionStateStore.Invalid();
        var ids = root.GetProperty("extensionIds").EnumerateArray().Select(item => item.GetString()!).ToArray();
        if (ids.Length is < 1 or > 64 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw InitialExtensionStateStore.Invalid();
        var catalog = new ExtensionCatalogVerifier(contracts, trust.Policy).Verify(
            Read(source, "index.json", StrictJson.MaximumBytes), Read(source, "catalog.json", StrictJson.MaximumBytes),
            Read(source, "catalog.sig.json", StrictJson.MaximumBytes), ExtensionStoreChannel.Stable);
        var names = ids.Select(id => catalog.Entries.Single(item => item.ExtensionId == id))
            .Select(item => item.ArtifactName + "-" + item.PackageVersion + ".zip")
            .Concat(["selection.json", "index.json", "catalog.json", "catalog.sig.json"]).Order(StringComparer.Ordinal).ToArray();
        if (!Directory.EnumerateFileSystemEntries(source).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(names))
            throw new IOException("initial_extensions_source_unowned");
        var files = names.Select(name =>
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(source, name));
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new InitialExtensionFile(name, input.Length, Convert.ToHexStringLower(SHA256.HashData(input)), PhysicalObjectIdentity.Read(path));
        }).ToArray();
        var settings = new HostSettingsStore(database).Read();
        var enable = ids.Where(id => new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(id) is null &&
            !settings.Values.Extensions.Any(item => item.ExtensionId == id)).ToArray();
        foreach (var entry in catalog.Entries.Where(item => ids.Contains(item.ExtensionId, StringComparer.Ordinal)))
        {
            var name = entry.ArtifactName + "-" + entry.PackageVersion + ".zip";
            var file = files.Single(item => item.Name == name);
            if (file.Sha256 != entry.ArtifactSha256 || file.SizeBytes != entry.ArtifactBytes)
                throw new IOException("initial_extensions_package_changed");
            _ = _blobs.PersistContent(Path.Combine(source, name),
                new(name, NexusPipeline.Core.Configuration.FileObjectKind.File, file.SizeBytes, file.Sha256), CancellationToken.None);
        }
        return _state.Save(new("nexus.extensions.initialization", 1, instance.InstanceId, source, PhysicalObjectIdentity.Read(source),
            files, _blobs.PersistPlan(catalog.PointerBytes()), _blobs.PersistPlan(catalog.CatalogBytes()),
            _blobs.PersistPlan(catalog.SignatureBytes()), ids, enable, false, false, Guid.NewGuid(), Guid.Empty, null, Guid.Empty, null));
    }

    private VerifiedExtensionCatalog VerifySource(InitialExtensionState state)
    {
        var trust = runtime.ReleaseTrust ?? throw new IOException("extension_release_trust_unprovisioned");
        var catalog = new ExtensionCatalogVerifier(contracts, trust.Policy).Verify(
            ReadBlob(state.Pointer), ReadBlob(state.Catalog), ReadBlob(state.Signature), ExtensionStoreChannel.Stable);
        foreach (var id in state.ExtensionIds)
        {
            var entry = catalog.Entries.Single(item => item.ExtensionId == id);
            var archive = state.Files.Single(file => file.Name == entry.ArtifactName + "-" + entry.PackageVersion + ".zip");
            if (archive.Sha256 != entry.ArtifactSha256 || archive.SizeBytes != entry.ArtifactBytes)
                throw new IOException("initial_extensions_package_changed");
            using var input = _blobs.OpenVerified(new("blobs/content/" + archive.Sha256 + ".bin", archive.SizeBytes, archive.Sha256));
            using var zip = ExtensionPackageArchive.Open(input, CancellationToken.None);
            var bytes = zip.ReadManifest(CancellationToken.None);
            var manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, id);
            VerifiedExtensionPackage.RequireManifest(entry, manifest, bytes, zip.Files);
            VerifiedExtensionPackage.RequireContents(manifest, zip);
        }
        return catalog;
    }

    private InitialExtensionState SetPreferences(InitialExtensionState state, bool enabled)
    {
        if (!enabled && state.PreferencesPrepared || state.EnableIds.Length == 0) return state;
        var store = new HostSettingsStore(database);
        if (state.PreferenceChange is null)
        {
            var saved = store.Read();
            var values = saved.Values with { Extensions = saved.Values.Extensions
                .Where(item => !state.EnableIds.Contains(item.ExtensionId, StringComparer.Ordinal))
                .Concat(state.EnableIds.Select(id => new ExtensionPreference(id, enabled))).OrderBy(item => item.ExtensionId, StringComparer.Ordinal).ToImmutableArray() };
            state = _state.Save(state with { PreferenceOperation = Guid.NewGuid(), PreferenceChange = new(saved.Revision, values) });
        }
        _ = lease.PublishExecution(() => store.Commit(HostSettingsStore.Identity(state.PreferenceOperation, Subject, state.PreferenceChange!),
            state.PreferenceChange!, DateTimeOffset.UtcNow));
        return _state.Save(state with { PreferencesPrepared = true, PreferenceChange = null });
    }

    private byte[] ReadBlob(BlobReference reference)
    {
        using var input = _blobs.OpenVerified(reference);
        if (input.Length > StrictJson.MaximumBytes) throw InitialExtensionStateStore.Invalid();
        return InitialExtensionStateStore.ReadBytes(input);
    }

    private static void Cleanup(InitialExtensionState state)
    {
        if (!Directory.Exists(state.SourcePath)) return;
        if (PhysicalObjectIdentity.Read(state.SourcePath) != state.SourceIdentity) throw new IOException("initial_extensions_source_changed");
        foreach (var file in state.Files)
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(state.SourcePath, file.Name));
            if (!File.Exists(path)) continue;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (PhysicalObjectIdentity.Read(path) != file.Identity || input.Length != file.SizeBytes ||
                    Convert.ToHexStringLower(SHA256.HashData(input)) != file.Sha256) throw new IOException("initial_extensions_cleanup_changed");
            File.Delete(path);
        }
        if (!Directory.EnumerateFileSystemEntries(state.SourcePath).Any()) Directory.Delete(state.SourcePath);
    }

    private static byte[] Read(string directory, string name, long maximum)
    {
        using var input = new FileStream(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(directory, name)), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 1 || input.Length > maximum) throw new IOException("initial_extensions_input_size_invalid");
        return InitialExtensionStateStore.ReadBytes(input);
    }

    private static bool RequiresSource(string source)
    {
        var directory = Path.GetDirectoryName(source)!;
        if (!File.Exists(Path.Combine(directory, "recipe.json"))) return false;
        using var recipe = StrictJson.Parse(Read(directory, "recipe.json", StrictJson.MaximumBytes));
        var root = recipe.RootElement;
        return root.GetProperty("packageKind").GetString() == "portable" &&
            root.GetProperty("bundledFiles").EnumerateArray().Any(item =>
                item.GetProperty("path").GetString()!.StartsWith("package-info/initial-extensions/", StringComparison.Ordinal));
    }

    private sealed class InitialPackageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("initial_extensions_cache_missing");
        }
    }
}
