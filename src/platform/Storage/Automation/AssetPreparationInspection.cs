using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

internal sealed class AssetPreparationInspection : IDisposable
{
    private readonly List<IDisposable> _held = [];
    internal bool AllContentReady { get; private set; }
    internal bool NoEffects { get; private set; }
    internal AssetEvidenceSnapshot? Evidence { get; private set; }

    internal AssetPreparationInspection(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
        FrozenAssetPreparation frozen, BlobReference reference, AssetPreparationWorkspace? workspace, bool verifyContents = true)
    {
        var plan = frozen.ReadWire();
        try
        {
            if (workspace is not null)
            {
                Evidence = new AssetEvidenceStore(instance, contracts, database).Read(frozen, reference.Sha256, workspace);
                _held.Add(Evidence);
            }
            if (!verifyContents) return;
            var workspacePath = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, AssetEvidenceCodec.WorkspacePath(plan)));
            NoEffects = workspace is null && !File.Exists(workspacePath) && !Directory.Exists(workspacePath);
            AllContentReady = workspace is not null;
            foreach (var root in plan.Roots)
            {
                if (!Directory.Exists(root.Path) || PhysicalObjectIdentity.Read(root.Path) != AssetEvidenceCodec.Physical(root.Identity))
                    { AllContentReady = false; continue; }
                _held.Add(new PhysicalDirectoryLease(root.Path, AssetEvidenceCodec.Physical(root.Identity)));
            }
            var blobs = new ImmutableBlobStore(instance, contracts);
            foreach (var asset in plan.Assets.DistinctBy(value => value.Content.Sha256)) InspectAsset(instance, blobs, asset);
        }
        catch { Dispose(); throw; }
    }

    private void InspectAsset(RuntimeInstance instance, ImmutableBlobStore blobs, AssetPreparationDefsAsset asset)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, asset.Content.RelativePath));
        _held.Add(new PhysicalDirectoryLease(Path.GetDirectoryName(path)!, AssetEvidenceCodec.Physical(asset.ParentIdentity)));
        if (Directory.Exists(path)) throw new IOException("asset_content_shape_invalid");
        var exists = File.Exists(path);
        if (!asset.WasPresent && exists) NoEffects = false;
        if (!exists)
        {
            if (asset.WasPresent) NoEffects = false;
            AllContentReady = false;
            return;
        }
        FileTreePhysicalIdentity? expected;
        if (asset.WasPresent) expected = asset.ExistingIdentity;
        else if (Evidence is not null && Evidence.Receipts.TryGetValue(AssetEvidenceStore.FileName(asset, "completed"), out var completion))
            expected = completion.Receipt.Identity;
        else { AllContentReady = false; return; }
        var input = blobs.OpenVerified(AssetPlanCodec.Reference(asset.Content));
        _held.Add(input);
        if (expected is null || PhysicalObjectIdentity.Read(input.SafeFileHandle) != AssetEvidenceCodec.Physical(expected))
        {
            NoEffects = false;
            AllContentReady = false;
        }
    }

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }
}
