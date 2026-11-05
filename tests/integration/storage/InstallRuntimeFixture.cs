using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstallRuntimeFixture : IDisposable
{
    internal readonly BoundPackageFixture Package;
    internal readonly PayloadApplyRoots Roots;
    internal readonly string Target;
    internal readonly ResourceOperationLease Root;
    internal readonly VerifiedExtensionPackage Verified;
    internal readonly PreparedExtensionPayload Prepared;
    internal readonly ExtensionActivator Activator;
    internal readonly PayloadAcceptanceResult Accepted;
    internal readonly string ActivationDirectory;
    internal readonly string ActivationNonce = Guid.NewGuid().ToString("N");

    internal InstallRuntimeFixture(bool? savedEnabled = null, string? managedMode = null, int payloadFiles = 0,
        Action<string>? managedPayloadEdit = null, InstalledPortDependencies? portDependencies = null, ExtensionCatalogFixture? catalog = null,
        System.Text.Json.Nodes.JsonObject? originalManifest = null, byte[]? originalManifestBytes = null,
        PayloadApplyRoots? payloadRoots = null, NexusPipeline.Core.Extensions.ExtensionStoreChannel channel = NexusPipeline.Core.Extensions.ExtensionStoreChannel.Stable,
        string? packageArchive = null, Action<System.Text.Json.Nodes.JsonObject>? managedManifestEdit = null,
        ExtensionActivator? existingActivator = null, long catalogSequence = 1, ExecutionProviderRegistry? providers = null)
    {
        if (payloadFiles is < 0 or > 300) throw new ArgumentOutOfRangeException(nameof(payloadFiles));
        ActivationDirectory = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!,
            "managed-activation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ActivationDirectory);
        var extensionId = originalManifest?["extensionId"]?.GetValue<string>() ?? "fixture-plugin";
        Package = packageArchive is null ? new(managedMode is null ? "data-specialized" : "managed-code",
            manifestEdit: manifest =>
            {
                if (managedMode is not null && originalManifest is null) manifest["managed"]!["entryType"] =
                    managedMode == "no-context" ? "Controlled.NoContextExtension" : "Controlled.ManagedExtension";
                if (managedMode is "media-wait" or "media-frame") manifest["capabilities"] = new System.Text.Json.Nodes.JsonArray("media.capture");
                if (managedMode == "schedule-wait") manifest["capabilities"] = new System.Text.Json.Nodes.JsonArray("schedule.register");
                if (managedMode is "frontend" or "frontend-ready")
                {
                    manifest["capabilities"] = new System.Text.Json.Nodes.JsonArray("ui.contribute");
                    manifest["permissions"]!["fileAccess"] = new System.Text.Json.Nodes.JsonArray("instance-data");
                    manifest["permissions"]!["networkOrigins"] = new System.Text.Json.Nodes.JsonArray("https://fixture.invalid");
                    manifest["frontend"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["entry"] = "frontend/main.js", ["styles"] = new System.Text.Json.Nodes.JsonArray("frontend/main.css"),
                        ["sdkVersion"] = "0.1.0"
                    };
                }
                if (managedMode?.StartsWith("native", StringComparison.Ordinal) == true && managedMode != "native-undeclared")
                    manifest["managed"]!["nativeLibraries"] = new System.Text.Json.Nodes.JsonArray(
                        managedMode == "native-managed" ? "entry.dll" : "native/nexus-native-fixture.dll");
                managedManifestEdit?.Invoke(manifest);
            },
            payloadEdit: directory =>
            {
                for (var index = 0; index < payloadFiles; index++)
                    File.WriteAllBytes(Path.Combine(directory, $"owned-{index:000}.bin"), [(byte)(index % 251)]);
                if (originalManifest is not null)
                {
                    File.Delete(Path.Combine(directory, "entry.dll"));
                    managedPayloadEdit?.Invoke(directory);
                    return;
                }
                if (managedMode is null) return;
                if (managedMode is "frontend" or "frontend-ready")
                {
                    Directory.CreateDirectory(Path.Combine(directory, "frontend"));
                    File.WriteAllText(Path.Combine(directory, "frontend/main.js"), "export const identity = 'fixture';\n");
                    File.WriteAllText(Path.Combine(directory, "frontend/main.css"), ".fixture { color: inherit; }\n");
                }
                var image = Environment.GetEnvironmentVariable("NEXUS_MANAGED_EXTENSION_FIXTURE_DLL")
                    ?? throw new IOException("NEXUS_MANAGED_EXTENSION_FIXTURE_DLL_required");
                File.Copy(image, Path.Combine(directory, "entry.dll"), overwrite: true);
                File.WriteAllBytes(Path.Combine(directory, "activation-input.json"),
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                    { Directory = ActivationDirectory, Nonce = ActivationNonce, Mode = managedMode }));
                if (managedMode.StartsWith("native", StringComparison.Ordinal))
                {
                    var native = Path.Combine(directory, "native"); Directory.CreateDirectory(native);
                    File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(native, "nexus-native-fixture.dll"));
                }
                managedPayloadEdit?.Invoke(directory);
            }, catalog: catalog, originalManifest: originalManifest, originalManifestBytes: originalManifestBytes, channel: channel) :
            new(packageArchive, originalManifest ?? throw new IOException("fixture_original_manifest_required"),
                originalManifestBytes ?? throw new IOException("fixture_original_manifest_bytes_required"), catalog, channel);
        try
        {
            Package.Publish(catalogSequence);
            if (savedEnabled is { } enabled)
            {
                var settings = new HostSettingsStore(Package.Catalog.Database); var before = settings.Read();
                var change = new HostSettingsChange(before.Revision,
                    before.Values with { Extensions = [new(extensionId, enabled)] });
                _ = settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "local", change), change, DateTimeOffset.UtcNow);
            }
            var registry = new RecoveryJournalRegistry(Package.Catalog.Database);
            var state = registry.ReadSnapshot(); _ = registry.PublishVerified(state.Revision);
            var parent = payloadRoots?.ExtensionPayloadRoot ?? Path.Combine(Package.Root, "installed"); Directory.CreateDirectory(parent);
            Roots = payloadRoots ?? new(Path.Combine(Package.Root, "host-payload"), parent); Target = Path.Combine(parent, extensionId);
            var claims = PhysicalResourceAdmission.Merge(new ResourceClaim[]
            {
                new("extension:" + extensionId, ResourceClaimMode.Mutation),
                new("extensions:catalog", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference),
                new("settings:host", savedEnabled.HasValue ? ResourceClaimMode.Reference : ResourceClaimMode.Mutation)
            }.Concat(PhysicalResourceAdmission.Read(Target)).Concat(PhysicalResourceAdmission.Read(
                ExtensionWorkspacePath.For(Target, Package.OperationId))));
            Root = Package.Catalog.Resources.TryAcquire(Package.OperationId, claims).Lease ?? throw new IOException("fixture_root_unavailable");
            Verified = Package.Prepare(Root);
            var candidate = Verified.CaptureCandidate(Root, Package.Blobs, default);
            Prepared = PreparedExtensionPayload.PrepareFirstInstall(Package.Catalog.Instance, Package.Catalog.Database,
                Package.Catalog.Lease, Package.Catalog.Resources, Package.Catalog.Index, Root, candidate, Roots,
                Package.Catalog.Contracts, "local", default);
            Accepted = new PayloadApplyAcceptance(Package.Catalog.Instance, Package.Catalog.Contracts, Package.Catalog.Database, Roots)
                .AcceptPrepared(Prepared, DateTimeOffset.UtcNow);
            Activator = existingActivator ?? new(Package.Catalog.Contracts, Package.Catalog.Lease, Package.Catalog.Resources, providers);
            if (existingActivator is null)
                _ = new InstalledPortFactory(Package.Catalog.Instance, Package.Catalog.Database, Package.Catalog.Lease,
                    Package.Catalog.Resources, Activator, Package.Catalog.Contracts, Package.Catalog.Index,
                    portDependencies ?? new(null, null, null, null, null, null));
        }
        catch { Dispose(); throw; }
    }

    internal PayloadApplySession Session(IPayloadPersistenceObserver? observer = null) => new(Package.Catalog.Instance,
        Package.Catalog.Database, Package.Catalog.Lease, Package.Catalog.Contracts, Roots, Activator, observer);
    internal ExtensionPhysicalJournal Physical() => new(Package.Catalog.Instance, Package.Catalog.Database, Package.Catalog.Contracts, Roots);
    internal PayloadCommitStore Committer(IPayloadPersistenceObserver? observer = null) =>
        new(Package.Catalog.Instance, Package.Catalog.Database, Package.Catalog.Contracts, Roots, observer);
    internal ExtensionPayloadBinding Binding() => new(Package.Catalog.Instance, Package.Catalog.Database, Package.Catalog.Contracts, Roots);
    public void Dispose() { Prepared?.Dispose(); Verified?.Dispose(); Root?.Dispose(); Package.Dispose(); }
}
