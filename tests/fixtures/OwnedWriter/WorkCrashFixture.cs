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

internal static class WorkCrashFixture
{
    internal sealed record Input(RuntimeInstance Instance, string Archive, byte[] PublicKey, Guid InitialOperationId,
        Guid RemoveOperationId, Guid CleanupOperationId, PayloadApplyRoots Roots, string Point, string Report);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var path]) return 2;
        OwnedFixtureEvidence.RequirePath(path);
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllBytes(path)) ?? throw new IOException("fixture_input_invalid");
        foreach (var item in new[] { input.Archive, input.Report, input.Roots.HostPayloadRoot, input.Roots.ExtensionPayloadRoot })
            OwnedFixtureEvidence.RequirePath(item);
        if (input.Point is not ("accepted" or "started" or "removed")) return 2;
        var contracts = new ContractReader(); var instance = new InstanceRoot(contracts).OpenExisting(input.Instance.RuntimeRoot);
        if (instance != input.Instance) return 2;
        using var lease = InstanceLease.Acquire(instance, contracts);
        var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(path)!, "child-inspection"),
            new JournalStartupAdmission(instance, contracts, input.Roots));
        var index = new StartupJournalRecovery(instance, contracts, database, input.Roots).Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var activator = new ExtensionActivator(contracts, lease, resources);
        var verifier = new ExtensionCatalogVerifier(contracts, new StoreTrustPolicy(
            new Dictionary<string, byte[]> { ["fixture-only"] = input.PublicKey }, new("https://store.example.test/metadata/store/1/"),
            [new Uri("https://assets.example.test/releases/download/")], 1, 1, new(0, 18, 0), "0.1.0"));
        var catalog = new ExtensionCatalogStore(instance, database, lease, resources, contracts, verifier);
        var settings = new HostSettingsStore(database); var original = settings.Read();
        var change = new HostSettingsChange(original.Revision, original.Values with { Extensions = [new("fixture-plugin", false)] });
        _ = settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "local", change), change, DateTimeOffset.UtcNow);
        ExtensionProjectionPublication.PublishIfDirty(instance, database, lease, contracts, input.Roots, resources, index);
        _ = await Apply(input.InitialOperationId, PayloadAction.Install);
        var removed = await Apply(input.RemoveOperationId, PayloadAction.Remove);
        ExtensionProjectionPublication.PublishIfDirty(instance, database, lease, contracts, input.Roots, resources, index);
        var bound = new ExtensionPayloadBinding(instance, database, contracts, input.Roots).Read(removed.JournalId, removed.Plan);
        _ = await new WorkCleanupService(instance, database, lease, contracts, input.Roots, resources, index, activator)
            .RunPageAsync(input.CleanupOperationId, "local", removed.JournalId, removed.Plan, null, null, default,
                new Barrier(input, instance, database, bound));
        return 1;

        async Task<PayloadAcceptanceResult> Apply(Guid operation, PayloadAction action)
        {
            var target = Path.Combine(input.Roots.ExtensionPayloadRoot, "fixture-plugin");
            var claims = PhysicalResourceAdmission.Merge(new ResourceClaim[]
            {
                new("extension:fixture-plugin", ResourceClaimMode.Mutation), new("extensions:catalog", ResourceClaimMode.Reference),
                new("blobs:content", ResourceClaimMode.Reference), new("settings:host", ResourceClaimMode.Reference)
            }.Concat(PhysicalResourceAdmission.Read(target)).Concat(PhysicalResourceAdmission.Read(ExtensionWorkspacePath.For(target, operation))));
            using var root = resources.TryAcquire(operation, claims).Lease ?? throw new IOException("fixture_root_unavailable");
            using var package = action == PayloadAction.Remove ? null : VerifiedExtensionPackage.Prepare(catalog,
                ExtensionStoreChannel.Stable, operation, root, "fixture-plugin", input.Archive, contracts, default);
            var candidate = package?.CaptureCandidate(root, new(instance, contracts), default);
            using var prepared = PreparedExtensionPayload.Prepare(instance, database, lease, resources, index, root,
                action, "fixture-plugin", action == PayloadAction.Install ? 0 : 1, settings.Read().Revision,
                candidate, input.Roots, contracts, "local", default);
            var accepted = new PayloadApplyAcceptance(instance, contracts, database, input.Roots).AcceptPrepared(prepared, DateTimeOffset.UtcNow);
            _ = await new PayloadApplySession(instance, database, lease, contracts, input.Roots, activator).ApplyAsync(prepared, default);
            ExtensionProjectionPublication.PublishIfDirty(instance, database, lease, contracts, input.Roots, resources, index);
            return accepted;
        }
    }

    private sealed class Barrier(Input input, RuntimeInstance instance, StateDatabase database, BoundExtensionPayload producer)
        : ICleanupPersistenceObserver
    {
        public void Reached(CleanupPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            var match = input.Point == "accepted" && point == CleanupPersistencePoint.AcceptanceCommitted ||
                input.Point == "started" && point == CleanupPersistencePoint.SqlProjectionCommitted && revision == 3 ||
                input.Point == "removed" && point == CleanupPersistencePoint.FileRemoved;
            if (!match) return;
            var registered = new RecoveryJournalRegistry(database).ReadJournal(journalId);
            var full = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, registered.PlanPath));
            OwnedFixtureEvidence.WriteReport(input.Report, new
            {
                status = "faultReady", processId = Environment.ProcessId, instance, journalId, revision, phase,
                Plan = new BlobReference(registered.PlanPath, new FileInfo(full).Length, registered.PlanDigest),
                ProducerJournalId = producer.Plan.JournalId, ProducerPlan = producer.PlanReference,
                Workspace = ExtensionWorkspacePath.For(producer.Plan),
                ProducerBytes = producer.Latest.Export(), MetadataRevision = 2
            });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
