using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class PreparedPayloadFixture
{
    internal static void Accept(JsonElement input, RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, IPayloadPersistenceObserver observer)
    {
        var initialArchive = input.GetProperty("initialArchive").GetString()!;
        var nextArchive = input.GetProperty("nextArchive").GetString()!;
        OwnedFixtureEvidence.RequirePath(initialArchive); OwnedFixtureEvidence.RequirePath(nextArchive);
        var key = input.GetProperty("publicKey").GetBytesFromBase64();
        var verifier = new ExtensionCatalogVerifier(contracts, new StoreTrustPolicy(
            new Dictionary<string, byte[]> { ["fixture-only"] = key }, new("https://store.example.test/metadata/store/1/"),
            [new Uri("https://assets.example.test/releases/download/")], 1, 1, new(0, 18, 0), "0.1.0"));
        var index = new StartupJournalRecovery(instance, contracts, database, roots).Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var activator = new ExtensionActivator(contracts, lease, resources);
        var catalog = new ExtensionCatalogStore(instance, database, lease, resources, contracts, verifier);
        var settings = new HostSettingsStore(database); var before = settings.Read();
        var change = new HostSettingsChange(before.Revision, before.Values with { Extensions = [new("fixture-plugin", false)] });
        _ = settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "local", change), change, DateTimeOffset.UtcNow);
        ExtensionProjectionPublication.PublishIfDirty(instance, database, lease, contracts, roots, resources, index);
        Apply(input.GetProperty("initialOperationId").GetGuid(), PayloadAction.Install, initialArchive, false);
        _ = catalog.Accept(Guid.NewGuid(), "local", verifier.Verify(input.GetProperty("nextPointer").GetBytesFromBase64(),
            input.GetProperty("nextCatalog").GetBytesFromBase64(), input.GetProperty("nextSignature").GetBytesFromBase64(), ExtensionStoreChannel.Stable));
        ExtensionProjectionPublication.PublishIfDirty(instance, database, lease, contracts, roots, resources, index);
        Apply(input.GetProperty("operationId").GetGuid(), PayloadAction.Upgrade, nextArchive, true);

        void Apply(Guid operationId, PayloadAction action, string archive, bool acceptOnly)
        {
            var target = Path.Combine(roots.ExtensionPayloadRoot, "fixture-plugin");
            var claims = PhysicalResourceAdmission.Merge(new ResourceClaim[]
            {
                new("extension:fixture-plugin", ResourceClaimMode.Mutation), new("extensions:catalog", ResourceClaimMode.Reference),
                new("settings:host", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)
            }.Concat(PhysicalResourceAdmission.Read(target)).Concat(PhysicalResourceAdmission.Read(ExtensionWorkspacePath.For(target, operationId))));
            using var root = resources.TryAcquire(operationId, claims).Lease ?? throw new IOException("fixture_root_blocked");
            using var verified = VerifiedExtensionPackage.Prepare(catalog, ExtensionStoreChannel.Stable, operationId, root,
                "fixture-plugin", archive, contracts, default);
            var candidate = verified.CaptureCandidate(root, new(instance, contracts), default);
            using var prepared = PreparedExtensionPayload.Prepare(instance, database, lease, resources, index, root, action,
                "fixture-plugin", action == PayloadAction.Install ? 0 : 1, settings.Read().Revision, candidate, roots, contracts, "fixture-payload", default);
            _ = new PayloadApplyAcceptance(instance, contracts, database, roots, acceptOnly ? observer : null)
                .AcceptPrepared(prepared, DateTimeOffset.UtcNow);
            if (acceptOnly) return;
            _ = new PayloadApplySession(instance, database, lease, contracts, roots, activator)
                .ApplyAsync(prepared, default).AsTask().GetAwaiter().GetResult();
            ExtensionProjectionPublication.PublishIfDirty(instance, database, lease, contracts, roots, resources, index);
        }
    }
}
