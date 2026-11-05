using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Automation;

internal sealed class AssetEvidenceCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal AssetPreparationWorkspace Workspace(AssetPreparationPlan plan, Guid writerId, PhysicalObjectIdentity identity) =>
        DecodeWorkspace(BoundedJson.Serialize(new AssetPreparationWorkspace
        {
            Format = "nexus.asset.preparation.workspace", Version = 1, InstanceId = plan.InstanceId,
            OperationId = plan.OperationId, JournalId = plan.JournalId, WriterId = writerId.ToString("D"),
            RelativePath = WorkspacePath(plan), Identity = Wire(identity), ParentIdentity = plan.WorkspaceParentIdentity
        }), plan);

    internal AssetPreparationWorkspace DecodeWorkspace(ReadOnlyMemory<byte> bytes, AssetPreparationPlan plan)
    {
        var value = contracts.Read<AssetPreparationWorkspace>(bytes, "asset-preparation-workspace.schema.json");
        if (value.InstanceId != instance.InstanceId.ToString("D") || value.InstanceId != plan.InstanceId ||
            value.OperationId != plan.OperationId || value.JournalId != plan.JournalId ||
            value.RelativePath != WorkspacePath(plan) || value.ParentIdentity != plan.WorkspaceParentIdentity ||
            value.Identity.VolumeSerial != value.ParentIdentity.VolumeSerial || value.Identity == value.ParentIdentity ||
            !CanonicalId(value.WriterId)) throw new IOException("asset_workspace_identity_unbound");
        return value;
    }

    internal AssetPreparationReceipt Receipt(AssetPreparationPlan plan, string planDigest, AssetPreparationDefsAsset asset,
        Guid writerId, PhysicalObjectIdentity? identity) => DecodeReceipt(BoundedJson.Serialize(new AssetPreparationReceipt
        {
            Format = "nexus.asset.preparation.receipt", Version = 1, InstanceId = plan.InstanceId,
            OperationId = plan.OperationId, JournalId = plan.JournalId, WriterId = writerId.ToString("D"),
            PlanDigest = planDigest, StepId = AssetPreparationAcceptance.Step(asset.Content.Sha256),
            Stage = identity is null ? "started" : "completed", Content = asset.Content,
            ParentIdentity = asset.ParentIdentity, Identity = identity is null ? null : Wire(identity)
        }), plan, planDigest, asset);

    internal AssetPreparationReceipt DecodeReceipt(ReadOnlyMemory<byte> bytes, AssetPreparationPlan plan, string planDigest,
        AssetPreparationDefsAsset asset)
    {
        var value = contracts.Read<AssetPreparationReceipt>(bytes, "asset-preparation-receipt.schema.json");
        if (value.InstanceId != instance.InstanceId.ToString("D") || value.InstanceId != plan.InstanceId ||
            value.OperationId != plan.OperationId || value.JournalId != plan.JournalId || value.PlanDigest != planDigest ||
            value.StepId != AssetPreparationAcceptance.Step(asset.Content.Sha256) || value.Content != asset.Content ||
            value.ParentIdentity != asset.ParentIdentity || asset.WasPresent || !CanonicalId(value.WriterId) ||
            (value.Stage == "completed") != (value.Identity is not null) ||
            value.Identity is { } identity && (identity.VolumeSerial != value.ParentIdentity.VolumeSerial || identity == value.ParentIdentity))
            throw new IOException("asset_receipt_identity_unbound");
        return value;
    }

    internal static string WorkspacePath(AssetPreparationPlan plan) => "asset-preparations/" + plan.JournalId;
    internal static PhysicalObjectIdentity Physical(FileTreePhysicalIdentity value) => new(value.VolumeSerial, value.FileId);
    private static FileTreePhysicalIdentity Wire(PhysicalObjectIdentity value) => new() { VolumeSerial = value.VolumeSerial, FileId = value.FileId };
    private static bool CanonicalId(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;
}
