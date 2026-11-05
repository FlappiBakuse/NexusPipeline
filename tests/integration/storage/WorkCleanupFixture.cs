using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class WorkCleanupFixture : IAsyncDisposable
{
    internal readonly ExtensionUpgradeFixture Upgrade;
    internal readonly BoundExtensionPayload Producer;
    internal InstallRuntimeFixture Original => Upgrade.Original;
    internal string Workspace => ExtensionWorkspacePath.For(Producer.Plan);

    private WorkCleanupFixture(ExtensionUpgradeFixture upgrade, BoundExtensionPayload producer)
    { Upgrade = upgrade; Producer = producer; }

    internal static async Task<WorkCleanupFixture> CreateAsync(PayloadAction action = PayloadAction.Upgrade,
        bool enabled = false, int files = 0)
    {
        var upgrade = await ExtensionUpgradeFixture.CreateAsync(enabled, originalPayloadFiles: files);
        try
        {
            upgrade.Prepare(action); _ = upgrade.Accept();
            _ = await upgrade.Session().ApplyAsync(upgrade.Prepared!, default);
            var producer = upgrade.Bound();
            upgrade.Prepared!.Dispose(); upgrade.Verified?.Dispose(); upgrade.Root!.Dispose();
            var owner = upgrade.Original.Package.Catalog;
            ExtensionProjectionPublication.PublishIfDirty(owner.Instance, owner.Database, owner.Lease,
                owner.Contracts, upgrade.Original.Roots, owner.Resources, owner.Index);
            return new(upgrade, producer);
        }
        catch { await upgrade.DisposeAsync(); throw; }
    }

    internal WorkCleanupService Service()
    {
        var owner = Original.Package.Catalog;
        return new(owner.Instance, owner.Database, owner.Lease, owner.Contracts, Original.Roots,
            owner.Resources, owner.Index, Original.Activator);
    }

    internal ValueTask<CleanupAcceptanceResult> RunAsync(Guid operationId, Guid? previous = null, BlobReference? previousPlan = null)
        => Service().RunPageAsync(operationId, "local", Producer.Plan.JournalId, Producer.PlanReference, previous, previousPlan, default);

    internal FrozenFileCleanup DirectPage(Guid operationId)
    {
        var owner = Original.Package.Catalog;
        var codec = new ExtensionWorkCodec(owner.Instance, owner.Database, owner.Contracts, Original.Roots);
        var physical = Producer.Physical[^1].Record;
        var proof = new ExtensionWorkProof(ExtensionWorkCodec.ProofFormat, 1, owner.Instance.InstanceId,
            Producer.Plan.OperationId, Producer.Plan.JournalId, Producer.PlanReference, Producer.Latest.Revision,
            Convert.ToHexStringLower(SHA256.HashData(Producer.Latest.Export())), Producer.CommitIntent!.Artifact, "before",
            Workspace, Producer.Plan.ParentIdentity, physical.WorkspaceIdentity!,
            WorkNodeDerivation.Derive(Producer, "before"));
        var origin = codec.ReadProof(codec.PersistProof(proof));
        var journalId = Guid.NewGuid();
        var page = new ExtensionWorkPage(ExtensionWorkCodec.PageFormat, 1, owner.Instance.InstanceId, operationId,
            journalId, origin.Reference, null, null, origin.Proof.Nodes.Take(WorkCleanupService.PageNodeCount).Select(node => node.Index).ToImmutableArray());
        return new(owner.Instance.InstanceId, operationId, journalId, []) { ExtensionWork = codec.ReadPage(codec.PersistPage(page)) };
    }

    public ValueTask DisposeAsync() => Upgrade.DisposeAsync();
}
