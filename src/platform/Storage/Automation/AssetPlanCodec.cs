using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed class AssetPlanCodec(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs)
{
    public PreparedAssetPreparation Freeze(AutomationImportPreview preview, ReadOnlyMemory<byte> input, Guid journalId)
    {
        if (input.Length is 0 or > StrictJson.MaximumBytes) throw new JsonException("invalid_json_size");
        var ownedInput = input.ToArray();
        if (!preview.CanApply || journalId == Guid.Empty || preview.OperationId == Guid.Empty ||
            Convert.ToHexStringLower(SHA256.HashData(ownedInput)) != preview.InputDigest)
            throw new IOException("asset_preparation_preview_unbound");
        var held = new List<IDisposable>();
        try
        {
            var wire = Capture(preview, ownedInput, journalId, held);
            var bytes = BoundedJson.Serialize(wire);
            var plan = Decode(bytes, held);
            var reference = blobs.PersistPlan(plan.Export());
            held.Add(blobs.OpenVerified(reference));
            return new(plan, reference, held);
        }
        catch { Release(held); throw; }
    }

    public FrozenAssetPreparation Read(BlobReference reference)
    {
        if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json" || reference.SizeBytes > StrictJson.MaximumBytes)
            throw new IOException("asset_preparation_plan_reference_invalid");
        var held = new List<IDisposable>();
        try { return Decode(ReadBytes(reference, held), held); }
        finally { Release(held); }
    }

    private AssetPreparationPlan Capture(AutomationImportPreview preview, ReadOnlyMemory<byte> input, Guid journalId, List<IDisposable> held)
    {
        var document = contracts.Read<AutomationBundle>(input, "automation-bundle.schema.json");
        var frozenInput = blobs.PersistPlan(input);
        var contentRoot = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "blobs", "content"));
        Directory.CreateDirectory(contentRoot);
        var parent = PhysicalObjectIdentity.Read(contentRoot);
        var workspaceParent = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "asset-preparations"));
        Directory.CreateDirectory(workspaceParent);
        var workspace = Path.Combine(workspaceParent, journalId.ToString("D"));
        if (File.Exists(workspace) || Directory.Exists(workspace)) throw new IOException("asset_preparation_workspace_not_exclusive");
        var roots = document.Roots.Select(root =>
        {
            var source = Guid.Parse(root.Id);
            var target = AutomationTemplateIdentity.RootId(preview.OperationId, source, preview.Bundle.IsScriptTemplate);
            var selected = preview.Roots.Single(value => value.RootId == target);
            if (PhysicalObjectIdentity.Read(selected.Path) != selected.Identity) throw new IOException("asset_preparation_root_changed");
            return new AssetPreparationDefsRoot { SourceRootId = source.ToString("D"), TargetRootId = target.ToString("D"),
                Path = selected.Path, Identity = Wire(selected.Identity) };
        }).ToArray();
        var assets = preview.Bundle.Attachments.Select(asset => CaptureAsset(asset, parent, held)).ToArray();
        return new()
        {
            Format = "nexus.asset.preparation-plan", Version = 1, InstanceId = instance.InstanceId.ToString("D"),
            OperationId = preview.OperationId.ToString("D"), JournalId = journalId.ToString("D"), OperationKind = "assetPrepare",
            Input = Wire(frozenInput), ExpectedCatalogRevision = preview.ExpectedRevision, PreviewDigest = preview.PreviewDigest,
            WorkspaceParentIdentity = Wire(PhysicalObjectIdentity.Read(workspaceParent)),
            Roots = roots, Assets = assets, ResourceKeys = Resources(assets)
        };
    }

    private AssetPreparationDefsAsset CaptureAsset(AutomationImportAttachment asset, PhysicalObjectIdentity parent, List<IDisposable> held)
    {
        var content = new BlobReference($"blobs/content/{asset.Sha256}.bin", asset.SizeBytes, asset.Sha256);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, content.RelativePath));
        if (Directory.Exists(path)) throw new IOException("asset_preparation_target_shape_invalid");
        FileTreePhysicalIdentity? identity = null;
        if (File.Exists(path))
        {
            var file = blobs.OpenVerified(content);
            held.Add(file);
            identity = Wire(PhysicalObjectIdentity.Read(file.SafeFileHandle));
        }
        return new() { AssetId = asset.Id.ToString("D"), PackagePath = asset.PackagePath, Content = Wire(content),
            WasPresent = identity is not null, ExistingIdentity = identity, ParentIdentity = Wire(parent) };
    }

    private FrozenAssetPreparation Decode(byte[] bytes, List<IDisposable> held)
    {
        var plan = contracts.Read<AssetPreparationPlan>(bytes, "asset-preparation-plan.schema.json");
        if (plan.InstanceId != instance.InstanceId.ToString("D") || blobs.Instance.InstanceId != instance.InstanceId ||
            Id(plan.OperationId) == Guid.Empty || Id(plan.JournalId) == Guid.Empty || plan.Input.SizeBytes > StrictJson.MaximumBytes ||
            plan.Input.RelativePath != $"blobs/plans/{plan.Input.Sha256}.json") throw new IOException("asset_preparation_identity_invalid");
        var selectedRoots = new Dictionary<Guid, string>();
        foreach (var root in plan.Roots)
        {
            var path = PhysicalPathPolicy.NormalizeSyntax(root.Path);
            if (path != root.Path || Within(path, instance.RuntimeRoot) || Within(instance.RuntimeRoot, path) ||
                !selectedRoots.TryAdd(Id(root.SourceRootId), path)) throw new IOException("asset_preparation_root_unbound");
        }
        var input = ReadBytes(Reference(plan.Input), held);
        var bundle = new AutomationBundleCodec(contracts).Decode(input, selectedRoots, plan.ExpectedCatalogRevision);
        bundle = AutomationTemplateIdentity.Remap(bundle, Id(plan.OperationId));
        if (plan.Roots.Count != bundle.Definitions.Roots.Length || plan.Roots.Any(root =>
                !bundle.Definitions.Roots.Any(value => value.Id == Id(root.TargetRootId) && value.Path == root.Path)))
            throw new IOException("asset_preparation_root_identity_unbound");
        VerifyAssets(plan, bundle);
        return new(bytes);
    }

    private static void VerifyAssets(AssetPreparationPlan plan, ParsedAutomationBundle bundle)
    {
        var byId = bundle.Attachments.ToDictionary(value => value.Id);
        if (plan.Assets.Count != byId.Count || plan.Assets.Select(value => value.AssetId).Distinct(StringComparer.Ordinal).Count() != plan.Assets.Count ||
            !plan.ResourceKeys.SequenceEqual(Resources(plan.Assets))) throw new IOException("asset_preparation_scope_unbound");
        foreach (var asset in plan.Assets)
        {
            if (!byId.TryGetValue(Id(asset.AssetId), out var expected) || asset.PackagePath != expected.PackagePath ||
                asset.Content.Sha256 != expected.Sha256 || asset.Content.SizeBytes != expected.SizeBytes ||
                asset.Content.RelativePath != $"blobs/content/{expected.Sha256}.bin" || asset.WasPresent != (asset.ExistingIdentity is not null) ||
                asset.ExistingIdentity is { } identity && identity.VolumeSerial != asset.ParentIdentity.VolumeSerial)
                throw new IOException("asset_preparation_asset_unbound");
            PhysicalPathPolicy.ValidateRelativePath(asset.PackagePath);
        }
        foreach (var group in plan.Assets.GroupBy(value => value.Content.Sha256, StringComparer.Ordinal))
            if (group.Any(value => value.Content != group.First().Content || value.WasPresent != group.First().WasPresent ||
                value.ExistingIdentity != group.First().ExistingIdentity || value.ParentIdentity != group.First().ParentIdentity))
                throw new IOException("asset_preparation_shared_content_unbound");
    }

    internal static IReadOnlyList<string> Resources(IEnumerable<AssetPreparationDefsAsset> assets) => assets.Select(value => "blob:" + value.Content.Sha256)
        .Append("automation:definitions").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    internal static BlobReference Reference(AssetPreparationDefsBlob value) => new(value.RelativePath, value.SizeBytes, value.Sha256);
    private static AssetPreparationDefsBlob Wire(BlobReference value) => new() { RelativePath = value.RelativePath, SizeBytes = value.SizeBytes, Sha256 = value.Sha256 };
    private static FileTreePhysicalIdentity Wire(PhysicalObjectIdentity value) => new() { VolumeSerial = value.VolumeSerial, FileId = value.FileId };

    private byte[] ReadBytes(BlobReference reference, List<IDisposable> held)
    {
        if (reference.SizeBytes > StrictJson.MaximumBytes) throw new IOException("asset_preparation_input_size_invalid");
        var input = blobs.OpenVerified(reference);
        held.Add(input);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static bool Within(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static Guid Id(string text) => Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && id.ToString("D") == text
        ? id : throw new IOException("asset_preparation_identity_invalid");
    private static void Release(List<IDisposable> held)
    {
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }
}
