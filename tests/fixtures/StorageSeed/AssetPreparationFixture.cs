using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal sealed record AssetPreparationFixture(string Root, RuntimeInstance Instance, StateDatabase Database,
    RecoveryIndex Index, PreparedAssetPreparation Prepared, AssetPreparationSources Sources, OperationIdentity Operation,
    InstanceLease InstanceLease, ResourceOperationLease Resources, ResourceLeaseManager ResourceManager, HostLifecycle Lifecycle) : IDisposable
{
    internal static AssetPreparationFixture Create(int assetCount = 1, string? selectedRoot = null, int scriptCount = 1)
    {
        var root = selectedRoot ?? Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "asset-execution-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var definitions = AutomationEdits.Apply(AutomationCatalog.Empty, AutomationDefinitionSeed.Create(root));
        if (scriptCount == 2) definitions = definitions with { Scripts = definitions.Scripts.Add(definitions.Scripts[0] with { Id = Guid.NewGuid(), Name = "Second" }) };
        var attachments = new List<AutomationImportAttachment>();
        var selected = new Dictionary<Guid, string>();
        for (var ordinal = 0; ordinal < assetCount; ordinal++)
        {
            var path = Path.Combine(root, "asset-" + ordinal + ".bin");
            var bytes = new byte[] { 5, 7, (byte)ordinal };
            File.WriteAllBytes(path, bytes);
            var asset = new AutomationImportAttachment(Guid.NewGuid(), "attachments/asset-" + ordinal + ".bin", bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
            attachments.Add(asset);
            selected.Add(asset.Id, path);
        }
        var input = new AutomationBundleCodec(new()).Encode(definitions, DateTimeOffset.UtcNow, selectedAttachments: attachments);
        var operationId = Guid.NewGuid();
        var preview = new AutomationImportPreviewer(instance, new(), database).Preview(operationId, input,
            definitions.Roots.ToDictionary(value => value.Id, value => value.Path), 0);
        var prepared = new AssetPlanCodec(instance, new(), new(instance, new())).Freeze(preview, input, Guid.NewGuid());
        var sources = new AssetPreparationSources(prepared.Plan, selected);
        var instanceLease = InstanceLease.Acquire(instance, new());
        var roots = new PayloadApplyRoots(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"));
        var index = new StartupJournalRecovery(instance, new(), database, roots).Recover(instanceLease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var manager = new ResourceLeaseManager(lifecycle);
        var admission = manager.TryAcquire(operationId, prepared.Plan.ReadWire().ResourceKeys.Select(value => new ResourceClaim(value, ResourceClaimMode.Mutation)).ToArray());
        var resources = admission.Lease ?? throw new IOException("fixture_resource_admission_failed");
        return new(root, instance, database, index, prepared, sources, AssetPreparationAcceptance.Identity(prepared.Plan.ReadWire(), "fixture"), instanceLease, resources, manager, lifecycle);
    }

    internal AutomationImportResult Execute(IAssetPreparationObserver? observer = null) =>
        new AssetPreparationExecutor(Instance, new(), Database, Index, observer).Execute(Operation, Prepared, Sources, InstanceLease, Resources);

    public void Dispose()
    {
        Resources.Dispose();
        Sources.Dispose();
        Prepared.Dispose();
        InstanceLease.Dispose();
    }
}
