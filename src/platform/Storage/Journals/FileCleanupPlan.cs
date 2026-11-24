using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed record FrozenCleanupItem(string StepId, BlobReference Blob, PhysicalObjectIdentity ParentIdentity, PhysicalObjectIdentity Identity);
public sealed record FrozenFileCleanup(Guid InstanceId, Guid OperationId, Guid JournalId, IReadOnlyList<FrozenCleanupItem> Items)
{
    internal BoundWorkPage? ExtensionWork { get; init; }
}

public sealed class CleanupPlanCodec(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs,
    StateDatabase? database = null, PayloadApplyRoots? payloadRoots = null)
{
    internal const int MaximumItems = 256;

    public FrozenFileCleanup Capture(Guid operationId, Guid journalId, IReadOnlyList<BlobReference> candidates)
    {
        if (candidates.Count is < 1 or > MaximumItems || candidates.Distinct().Count() != candidates.Count)
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
            plan.OperationId == Guid.Empty || plan.JournalId == Guid.Empty || plan.Items.Count > MaximumItems ||
            plan.Items.Select(item => item.Blob.Sha256).Distinct(StringComparer.Ordinal).Count() != plan.Items.Count)
            throw new IOException("cleanup_plan_identity_invalid");
        if (plan.ExtensionWork is { } work)
        {
            if (plan.Items.Count != 0 || work.Page.InstanceId != plan.InstanceId ||
                work.Page.OperationId != plan.OperationId || work.Page.JournalId != plan.JournalId ||
                work.Page.Indices.IsDefaultOrEmpty || work.Page.Indices.Length > MaximumItems) throw new IOException("extension_work_plan_unbound");
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

internal static class CleanupCandidateFiles
{
    internal static Guid? SecretId(BlobReference item)
    {
        const string prefix = "secrets/payloads/";
        if (!item.RelativePath.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var suffix = "-" + item.Sha256 + ".bin";
        var name = item.RelativePath[prefix.Length..];
        if (name.Length != 36 + suffix.Length || !name.EndsWith(suffix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[..36], "D", out var id) || id == Guid.Empty || name[..36] != id.ToString("D"))
            throw new IOException("cleanup_secret_path_invalid");
        return id;
    }

    internal static void Validate(BlobReference item)
    {
        var secret = SecretId(item);
        if (item.Sha256.Length != 64 || item.Sha256.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            (secret is null && item.RelativePath != $"blobs/content/{item.Sha256}.bin") ||
            item.SizeBytes < (secret is null ? 0 : 1) || item.SizeBytes > (secret is null ? 1_073_741_824 : 1_048_576))
            throw new IOException("cleanup_content_path_invalid");
    }

    internal static string ResourceKey(BlobReference item)
    {
        Validate(item);
        return SecretId(item) is { } id ? "secret-payload:" + id.ToString("D") : "blob:" + item.Sha256;
    }

    internal static FileStream OpenVerified(RuntimeInstance instance, ImmutableBlobStore blobs, BlobReference item)
    {
        Validate(item);
        // The enclosing recovery may need DELETE access after this candidate inspection closes.
        if (SecretId(item) is null) return blobs.OpenVerified(item, retainInScope: false);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, item.RelativePath));
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length != item.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(input)) != item.Sha256)
                throw new IOException("cleanup_secret_bytes_unverified");
            input.Position = 0;
            return input;
        }
        catch { input.Dispose(); throw; }
    }
}

internal static class FileCleanupItems
{
    internal static IReadOnlyList<string> Steps(FrozenFileCleanup plan) => plan.ExtensionWork is { } work
        ? Array.AsReadOnly(work.Page.Indices.Select((_, index) => $"remove-{index:000}").ToArray())
        : Array.AsReadOnly(plan.Items.Select(item => item.StepId).ToArray());

    internal static IReadOnlyList<string> Keys(FrozenFileCleanup plan) => plan.ExtensionWork is { } work
        ? Array.AsReadOnly(ExtensionWorkClaims.Read(work.Origin).Select(item => item.ResourceKey).ToArray())
        : Array.AsReadOnly(plan.Items.Select(item => CleanupCandidateFiles.ResourceKey(item.Blob)).Order(StringComparer.Ordinal).ToArray());

    internal static IReadOnlyList<ResourceClaim> Claims(FrozenFileCleanup plan) => plan.ExtensionWork is { } work
        ? ExtensionWorkClaims.Read(work.Origin)
        : Array.AsReadOnly(Keys(plan).Select(key => new ResourceClaim(key, ResourceClaimMode.Mutation)).ToArray());

    internal static ExtensionWorkNode WorkNode(FrozenFileCleanup plan, string step)
    {
        var work = plan.ExtensionWork ?? throw new IOException("extension_work_plan_required");
        var index = Steps(plan).ToList().FindIndex(value => value == step);
        return index >= 0 ? work.Origin.Proof.Nodes[work.Page.Indices[index]] : throw new IOException("extension_work_step_unbound");
    }
}
