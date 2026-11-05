using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed record AssetReceiptEvidence(AssetPreparationReceipt Receipt, string Sha256);

public sealed class AssetEvidenceSnapshot : IDisposable
{
    private List<IDisposable>? _held;
    public IReadOnlyDictionary<string, AssetReceiptEvidence> Receipts { get; }

    internal AssetEvidenceSnapshot(Dictionary<string, AssetReceiptEvidence> receipts, List<IDisposable> held)
        { Receipts = new System.Collections.ObjectModel.ReadOnlyDictionary<string, AssetReceiptEvidence>(receipts); _held = held; }

    public void Dispose()
    {
        var held = Interlocked.Exchange(ref _held, null);
        if (held is null) return;
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }
}

public sealed class AssetEvidenceStore(RuntimeInstance instance, ContractReader contracts, StateDatabase database)
{
    private readonly AssetEvidenceCodec _codec = new(instance, contracts);

    public AssetPreparationWorkspace CreateWorkspace(FrozenAssetPreparation frozen, string planDigest)
    {
        var plan = frozen.ReadWire();
        VerifyDigest(frozen, planDigest);
        RequireExecuting(plan, planDigest);
        var path = PathFor(AssetEvidenceCodec.WorkspacePath(plan));
        using var parent = new PhysicalDirectoryLease(Path.GetDirectoryName(path)!, AssetEvidenceCodec.Physical(plan.WorkspaceParentIdentity));
        ExclusiveDirectory.Create(path);
        return _codec.Workspace(plan, JournalWriterStore.CurrentWriter.WriterId, PhysicalObjectIdentity.Read(path));
    }

    public AssetReceiptEvidence Write(FrozenAssetPreparation frozen, string planDigest, AssetPreparationWorkspace workspace,
        AssetPreparationReceipt receipt)
    {
        var plan = frozen.ReadWire();
        VerifyDigest(frozen, planDigest);
        RequireExecuting(plan, planDigest);
        var expected = _codec.DecodeWorkspace(BoundedJson.Serialize(workspace), plan);
        if (expected.WriterId != JournalWriterStore.CurrentWriter.WriterId.ToString("D") || receipt.WriterId != expected.WriterId)
            throw new IOException("asset_receipt_writer_unbound");
        var asset = plan.Assets.FirstOrDefault(value => AssetPreparationAcceptance.Step(value.Content.Sha256) == receipt.StepId)
            ?? throw new IOException("asset_receipt_step_unbound");
        var bytes = BoundedJson.Serialize(receipt);
        var verified = _codec.DecodeReceipt(bytes, plan, planDigest, asset);
        var path = PathFor(expected.RelativePath);
        using var parent = new PhysicalDirectoryLease(Path.GetDirectoryName(path)!, AssetEvidenceCodec.Physical(expected.ParentIdentity));
        using var directory = new PhysicalDirectoryLease(path, AssetEvidenceCodec.Physical(expected.Identity));
        if (verified.Stage == "completed") VerifyStarted(path, plan, planDigest, asset, receipt.WriterId);
        var target = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(path, FileName(asset, verified.Stage)));
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
        return new(verified, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public AssetEvidenceSnapshot Read(FrozenAssetPreparation frozen, string planDigest, AssetPreparationWorkspace workspace)
    {
        var plan = frozen.ReadWire();
        VerifyDigest(frozen, planDigest);
        var expected = _codec.DecodeWorkspace(BoundedJson.Serialize(workspace), plan);
        if (!new JournalWriterStore(database).IsRegistered(Guid.Parse(plan.JournalId), Guid.Parse(expected.WriterId)))
            throw new IOException("asset_workspace_writer_unregistered");
        var path = PathFor(expected.RelativePath);
        var held = new List<IDisposable>();
        try
        {
            held.Add(new PhysicalDirectoryLease(Path.GetDirectoryName(path)!, AssetEvidenceCodec.Physical(expected.ParentIdentity)));
            held.Add(new PhysicalDirectoryLease(path, AssetEvidenceCodec.Physical(expected.Identity)));
            var allowed = plan.Assets.Where(value => !value.WasPresent).DistinctBy(value => value.Content.Sha256)
                .SelectMany(asset => new[] { (Name: FileName(asset, "started"), Asset: asset, Stage: "started"),
                    (Name: FileName(asset, "completed"), Asset: asset, Stage: "completed") }).ToDictionary(value => value.Name, StringComparer.Ordinal);
            var entries = Directory.EnumerateFileSystemEntries(path).Take(513).Order(StringComparer.Ordinal).ToArray();
            if (entries.Length > 512) throw new IOException("asset_evidence_inventory_limit");
            var receipts = new Dictionary<string, AssetReceiptEvidence>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (!allowed.TryGetValue(name, out var item)) throw new IOException("asset_evidence_entry_unknown");
                var bytes = Open(entry, held);
                var receipt = _codec.DecodeReceipt(bytes, plan, planDigest, item.Asset);
                if (receipt.Stage != item.Stage || receipt.WriterId != expected.WriterId) throw new IOException("asset_evidence_scope_unbound");
                receipts.Add(name, new(receipt, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
            foreach (var asset in plan.Assets.Where(value => !value.WasPresent))
                if (receipts.ContainsKey(FileName(asset, "completed")) && !receipts.ContainsKey(FileName(asset, "started")))
                    throw new IOException("asset_completed_without_started_receipt");
            return new(receipts, held);
        }
        catch { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); throw; }
    }

    internal static string FileName(AssetPreparationDefsAsset asset, string stage) => asset.Content.Sha256 + "." + stage + ".json";

    private static void VerifyDigest(FrozenAssetPreparation frozen, string digest)
    {
        if (Convert.ToHexStringLower(SHA256.HashData(frozen.Export())) != digest) throw new IOException("asset_evidence_plan_digest_unbound");
    }

    private void RequireExecuting(AssetPreparationPlan plan, string planDigest)
    {
        if (plan.InstanceId != instance.InstanceId.ToString("D") || database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("asset_evidence_instance_unbound");
        var id = Guid.Parse(plan.JournalId);
        var registered = new RecoveryJournalRegistry(database).ReadJournal(id);
        var gate = new Sqlite.JournalEffectGate(database).Read(id);
        if (registered.OperationId.ToString("D") != plan.OperationId || registered.PlanDigest != planDigest ||
            registered.Kind.ToString() != "AssetPrepare" || registered.Phase != JournalPhase.Applying ||
            !registered.ResourceKeys.SequenceEqual(plan.ResourceKeys) || gate.State != EffectGateState.Entered ||
            !new JournalWriterStore(database).IsRegistered(id, JournalWriterStore.CurrentWriter.WriterId))
            throw new IOException("asset_evidence_execution_unbound");
    }

    private void VerifyStarted(string path, AssetPreparationPlan plan, string digest, AssetPreparationDefsAsset asset, string writer)
    {
        var held = new List<IDisposable>();
        try
        {
            var receipt = _codec.DecodeReceipt(Open(Path.Combine(path, FileName(asset, "started")), held), plan, digest, asset);
            if (receipt.Stage != "started" || receipt.WriterId != writer) throw new IOException("asset_started_receipt_unbound");
        }
        finally { foreach (var file in held) file.Dispose(); }
    }

    private static byte[] Open(string path, List<IDisposable> held)
    {
        PhysicalPathPolicy.NormalizeWithoutLinks(path);
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        held.Add(input);
        if (input.Length is 0 or > 8192) throw new IOException("asset_evidence_byte_limit");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private string PathFor(string relative) => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, relative));
}
