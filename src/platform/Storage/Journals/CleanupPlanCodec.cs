using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed record FrozenCleanupItem(string StepId, BlobReference Blob, PhysicalObjectIdentity ParentIdentity, PhysicalObjectIdentity Identity);
public sealed record FrozenFileCleanup(Guid InstanceId, Guid OperationId, Guid JournalId, IReadOnlyList<FrozenCleanupItem> Items)
{
    internal BoundWorkPage? ExtensionWork { get; init; }
}

public sealed class CleanupPlanCodec(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs,
    StateDatabase? database = null, PayloadApplyRoots? payloadRoots = null)
{
    public FrozenFileCleanup Capture(Guid operationId, Guid journalId, IReadOnlyList<BlobReference> candidates)
    {
        if (candidates.Count is < 1 or > 256 || candidates.Distinct().Count() != candidates.Count)
            throw new ArgumentException("cleanup_candidates_invalid");
        var items = new List<FrozenCleanupItem>();
        foreach (var blob in candidates.OrderBy(item => item.Sha256, StringComparer.Ordinal))
        {
            CleanupCandidateFiles.Validate(blob);
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, blob.RelativePath));
            using var input = CleanupCandidateFiles.OpenVerified(instance, blobs, blob);
            items.Add(new($"remove-{items.Count:000}", blob, PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!),
                PhysicalObjectIdentity.Read(input.SafeFileHandle)));
        }
        var plan = new FrozenFileCleanup(instance.InstanceId, operationId, journalId, items.AsReadOnly());
        Validate(plan);
        return plan;
    }

    public BlobReference Persist(FrozenFileCleanup plan)
    {
        Validate(plan);
        var wire = new FileCleanupPlan
        {
            Format = "nexus.file.cleanup-plan", Version = 1, InstanceId = plan.InstanceId.ToString("D"),
            OperationId = plan.OperationId.ToString("D"), JournalId = plan.JournalId.ToString("D"), OperationKind = "fileCleanup",
            Items = plan.Items.Select(item => new FileCleanupPlanItemsItem
            {
                StepId = item.StepId, RelativePath = item.Blob.RelativePath, SizeBytes = item.Blob.SizeBytes, Sha256 = item.Blob.Sha256,
                ParentIdentity = new() { VolumeSerial = item.ParentIdentity.VolumeSerial, FileId = item.ParentIdentity.FileId },
                Identity = new() { VolumeSerial = item.Identity.VolumeSerial, FileId = item.Identity.FileId }
            }).ToArray(),
            ExtensionWork = plan.ExtensionWork is { } work ? new FileCleanupPlanExtensionWork
            { RelativePath = work.Reference.RelativePath, SizeBytes = work.Reference.SizeBytes, Sha256 = work.Reference.Sha256 } : null
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(wire);
        using var verified = contracts.Read(bytes, "file-cleanup-plan.schema.json");
        return blobs.PersistPlan(bytes);
    }

    public FrozenFileCleanup Read(BlobReference reference)
    {
        if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json" || reference.SizeBytes > StrictJson.MaximumBytes)
            throw new IOException("cleanup_plan_reference_invalid");
        using var input = blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        var wire = contracts.Read<FileCleanupPlan>(bytes, "file-cleanup-plan.schema.json");
        var plan = new FrozenFileCleanup(Guid.Parse(wire.InstanceId), Guid.Parse(wire.OperationId), Guid.Parse(wire.JournalId),
            Array.AsReadOnly(wire.Items.Select(item => new FrozenCleanupItem(item.StepId, new(item.RelativePath, item.SizeBytes, item.Sha256),
                new(item.ParentIdentity.VolumeSerial, item.ParentIdentity.FileId), new(item.Identity.VolumeSerial, item.Identity.FileId))).ToArray()));
        if (wire.ExtensionWork is { } work)
        {
            if (database is null || payloadRoots is null) throw new IOException("extension_work_context_required");
            plan = plan with { ExtensionWork = new ExtensionWorkCodec(instance, database, contracts, payloadRoots)
                .ReadPage(new(work.RelativePath, work.SizeBytes, work.Sha256)) };
        }
        Validate(plan);
        return plan;
    }

    private void Validate(FrozenFileCleanup plan)
    {
        if (plan.InstanceId != instance.InstanceId || blobs.Instance.InstanceId != instance.InstanceId ||
            plan.OperationId == Guid.Empty || plan.JournalId == Guid.Empty || plan.Items.Count > 256 ||
            plan.Items.Select(item => item.Blob.Sha256).Distinct(StringComparer.Ordinal).Count() != plan.Items.Count)
            throw new IOException("cleanup_plan_identity_invalid");
        if (plan.ExtensionWork is { } work)
        {
            if (plan.Items.Count != 0 || work.Page.InstanceId != plan.InstanceId ||
                work.Page.OperationId != plan.OperationId || work.Page.JournalId != plan.JournalId ||
                work.Page.Indices.IsDefaultOrEmpty || work.Page.Indices.Length > 256) throw new IOException("extension_work_plan_unbound");
            return;
        }
        if (plan.Items.Count == 0) throw new IOException("cleanup_plan_identity_invalid");
        for (var index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            CleanupCandidateFiles.Validate(item.Blob);
            if (item.StepId != $"remove-{index:000}" ||
                item.Blob.SizeBytes is < 0 or > 1_073_741_824 || !IsHex(item.Blob.Sha256, 64) ||
                !IsHex(item.ParentIdentity.VolumeSerial, 16) || !IsHex(item.ParentIdentity.FileId, 32) ||
                !IsHex(item.Identity.VolumeSerial, 16) || !IsHex(item.Identity.FileId, 32) ||
                item.ParentIdentity.VolumeSerial != item.Identity.VolumeSerial)
                throw new IOException("cleanup_plan_item_invalid");
        }
    }

    private static bool IsHex(string value, int length) => value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

