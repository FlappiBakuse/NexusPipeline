using NexusPipeline.Core.Configuration;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using UpdatePackageRecipe = NexusPipeline.Platform.Storage.Maintenance.UpdateRecipe;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PayloadPlanTests
{
    [Theory(DisplayName = "Payload Intent Freezes Exact Action Trees Resources And Original Objects Without Changing The Site")]
    [InlineData(JournalKind.UpdateApply, PayloadAction.Upgrade)]
    [InlineData(JournalKind.ExtensionApply, PayloadAction.Install)]
    [InlineData(JournalKind.ExtensionApply, PayloadAction.Upgrade)]
    [InlineData(JournalKind.ExtensionApply, PayloadAction.Remove)]
    public void FreezesPayloadIntent(JournalKind kind, PayloadAction action)
    {
        using var fixture = PayloadTestFixture.Create(kind, action);
        var maintenance = kind == JournalKind.UpdateApply;
        var target = maintenance ? fixture.Roots.HostPayloadRoot : Path.Combine(fixture.Roots.ExtensionPayloadRoot, "fixture-plugin");
        var before = fixture.Scanner.Capture(target, default);
        var source = maintenance ? fixture.Plan.MaintenanceSource! : fixture.Plan.ExtensionSource!;
        var inputContracts = new ContractReader();
        var archive = maintenance
            ? new UpdateSourceCodec(fixture.Instance, inputContracts).ReadMetadata(source).Archive
            : new PayloadSourceCodec(fixture.Instance, inputContracts).ReadMetadata(source).Package?.Archive;
        using var prepared = fixture.Codec.Freeze(fixture.Plan);
        var reference = prepared.Reference;
        var planPath = Path.Combine(fixture.Instance.RuntimeRoot, reference.RelativePath);
        var bytes = File.ReadAllBytes(planPath);
        Assert.Equal(bytes.LongLength, reference.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), reference.Sha256);
        Assert.Equal($"blobs/plans/{reference.Sha256}.json", reference.RelativePath);
        using var document = JsonDocument.Parse(bytes);
        var wire = document.RootElement;
        Assert.Equal("nexus.payload.apply-plan", wire.GetProperty("format").GetString());
        Assert.Equal(1, wire.GetProperty("version").GetInt32());
        Assert.Equal(fixture.Instance.InstanceId, wire.GetProperty("instanceId").GetGuid());
        Assert.Equal(fixture.Plan.OperationId, wire.GetProperty("operationId").GetGuid());
        Assert.Equal(fixture.Plan.JournalId, wire.GetProperty("journalId").GetGuid());
        Assert.Equal(maintenance ? "updateApply" : "extensionApply", wire.GetProperty("operationKind").GetString());
        Assert.Equal(action.ToString().ToLowerInvariant(), wire.GetProperty("action").GetString());
        Assert.Equal(target, wire.GetProperty("targetPath").GetString());
        Assert.Equal(!maintenance, wire.TryGetProperty("extensionId", out var extensionId));
        if (!maintenance) Assert.Equal("fixture-plugin", extensionId.GetString());
        var sourceWire = wire.GetProperty(maintenance ? "maintenanceSource" : "extensionSource");
        Assert.Equal(source.RelativePath, sourceWire.GetProperty("relativePath").GetString());
        Assert.Equal(source.SizeBytes, sourceWire.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(source.Sha256, sourceWire.GetProperty("sha256").GetString());
        var locked = Assert.Throws<IOException>(() => File.WriteAllBytes(planPath, [0]));
        Assert.Equal(32, locked.HResult & 0xffff);
        prepared.Dispose();

        var contracts = new ContractReader();
        var blobs = new ImmutableBlobStore(fixture.Instance, contracts);
        var restored = new PayloadPlanCodec(fixture.Instance, contracts, blobs, fixture.Roots).Read(reference);
        Assert.Equal(kind, restored.Kind);
        Assert.Equal(action, restored.Action);
        Assert.Equal(source, maintenance ? restored.MaintenanceSource : restored.ExtensionSource);
        Assert.Equal(target, restored.TargetPath);
        Assert.Equal(PhysicalObjectIdentity.Read(Path.GetDirectoryName(target)!), restored.ParentIdentity);
        var original = maintenance ? ApplicationTree.Capture(fixture.Scanner, target, default) : before;
        Assert.Equal(original.Entries, restored.Before.Snapshot.Entries);
        Assert.Equal(original.Entries.Select(entry => entry.RelativePath), restored.Before.OriginalObjects.Select(item => item.RelativePath));
        foreach (var item in restored.Before.OriginalObjects)
            Assert.Equal(PhysicalObjectIdentity.Read(Path.Combine(target, item.RelativePath)), item.Identity);

        var resource = maintenance ? "maintenance:host-payload" : "extension:fixture-plugin";
        Assert.Contains(resource, wire.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(resource, restored.ResourceKeys);
        if (action == PayloadAction.Remove)
        {
            Assert.Equal(FileObjectKind.Missing, restored.Candidate.Snapshot.Shape);
            Assert.Empty(restored.Candidate.Snapshot.Entries);
            Assert.Null(archive);
        }
        else
        {
            Assert.NotNull(archive);
            using var package = new ZipArchive(blobs.OpenVerified(archive));
            var expectedFiles = package.Entries.Where(entry => entry.Name.Length != 0 &&
                    (!maintenance || !UpdatePackageRecipe.Metadata.Contains(entry.FullName)))
                .Select(entry =>
                {
                    using var input = entry.Open();
                    return new FileTreeEntry(entry.FullName, FileObjectKind.File, entry.Length, Convert.ToHexStringLower(SHA256.HashData(input)));
                }).OrderBy(entry => entry.RelativePath, StringComparer.Ordinal);
            Assert.Equal(expectedFiles, restored.Candidate.Snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File)
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal));
        }
        var after = fixture.Scanner.Capture(target, default);
        Assert.Equal(before.Shape, after.Shape);
        Assert.Equal(before.Entries, after.Entries);
        Assert.Equal(bytes, File.ReadAllBytes(planPath));
    }

    [Theory(DisplayName = "Unbound Payload Plans Are Rejected Before Plan Publication Or Target Mutation")]
    [InlineData("instance")]
    [InlineData("kind")]
    [InlineData("operation")]
    [InlineData("journal")]
    [InlineData("target")]
    [InlineData("resource")]
    [InlineData("missing-identity")]
    [InlineData("cross-volume")]
    [InlineData("update-remove")]
    [InlineData("missing-extension-id")]
    [InlineData("install-existing")]
    public void RejectsUnboundIntent(string fault)
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.ExtensionApply, PayloadAction.Upgrade);
        var plan = fixture.Plan;
        var changed = fault switch
        {
            "instance" => plan with { InstanceId = Guid.NewGuid() },
            "kind" => plan with { Kind = JournalKind.RunAttempt },
            "operation" => plan with { OperationId = Guid.Empty },
            "journal" => plan with { JournalId = Guid.Empty },
            "target" => plan with { TargetPath = fixture.Root },
            "resource" => plan with { ResourceKeys = ["maintenance:host-payload"] },
            "missing-identity" => plan with { Before = plan.Before with { OriginalObjects = [] } },
            "cross-volume" => plan with { ParentIdentity = plan.ParentIdentity with { VolumeSerial = "ffffffffffffffff" } },
            "update-remove" => plan with { Kind = JournalKind.UpdateApply, Action = PayloadAction.Remove,
                ExtensionId = null, TargetPath = fixture.Roots.HostPayloadRoot, ResourceKeys = ["maintenance:host-payload"] },
            "missing-extension-id" => plan with { ExtensionId = null },
            "install-existing" => plan with { Action = PayloadAction.Install },
            _ => throw new InvalidOperationException()
        };
        var before = fixture.Scanner.Capture(plan.TargetPath, default);
        var plans = Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "plans");
        var existing = Directory.EnumerateFiles(plans).Order(StringComparer.Ordinal).ToArray();
        var reason = fault switch
        {
            "instance" or "kind" or "operation" or "journal" => "payload_plan_identity_invalid",
            "target" or "missing-extension-id" => "payload_target_not_owned",
            "resource" or "cross-volume" or "install-existing" => "extension_payload_source_unbound",
            "missing-identity" => "payload_original_identity_incomplete",
            "update-remove" => "maintenance_payload_tree_changed",
            _ => throw new InvalidOperationException()
        };
        var rejected = Assert.Throws<IOException>(() => fixture.Codec.Freeze(changed));
        Assert.Equal(reason, rejected.Message);
        var after = fixture.Scanner.Capture(plan.TargetPath, default);
        Assert.Equal(before.Shape, after.Shape);
        Assert.Equal(before.Entries, after.Entries);
        Assert.Equal(existing, Directory.EnumerateFiles(plans).Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "Published Payload Plan Rejects Damaged Candidate Bytes And Its Lease Does Not Expose A Disposed Plan")]
    public void RejectsDamagedCandidates()
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.ExtensionApply, PayloadAction.Install);
        var prepared = fixture.Codec.Freeze(fixture.Plan);
        var reference = prepared.Reference;
        var candidate = new NexusPipeline.Platform.Storage.Extensions.PayloadSourceCodec(fixture.Instance, new())
            .ReadMetadata(prepared.Plan.ExtensionSource!).Package!.Archive;
        prepared.Dispose();
        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => prepared.Plan);
        Assert.Throws<ObjectDisposedException>(() => prepared.Reference);
        File.WriteAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, candidate.RelativePath), [99, 88]);
        Assert.Throws<IOException>(() => fixture.Codec.Read(reference));
        Assert.Equal(FileObjectKind.Missing, fixture.Scanner.Capture(fixture.Plan.TargetPath, default).Shape);
    }

}
