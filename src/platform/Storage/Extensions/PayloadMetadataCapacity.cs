using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class PayloadMetadataCapacity
{
    internal static void Require(FrozenPayloadApply plan, ExtensionPayloadSource source)
    {
        if (source.ExpectedInstalledRevision >= 9007199254740991 ||
            source.InitializeEnabledPreference && source.SettingsRevision >= 9007199254740991 ||
            plan.ResourceKeys.Count - plan.Before.OriginalObjects.Count + plan.Candidate.Snapshot.Entries.Count > 4096)
            throw Invalid();
        var reference = new BlobReference("blobs/plans/" + new string('f', 64) + ".json", 2097152, new string('f', 64));
        var payload = plan.Action == PayloadAction.Remove
            ? new ArchivedFileTree(new(FileObjectKind.Missing, []), [], [])
            : plan.Candidate with
            {
                OriginalObjects = plan.Candidate.Snapshot.Entries.Select(item => new PhysicalTreeObject(item.RelativePath,
                    new PhysicalObjectIdentity(new string('f', 16), new string('f', 32)))).ToArray()
            };
        try
        {
            _ = BoundedJson.Serialize(new
            {
                format = InstalledFootprintCodec.Format, version = 1, instanceId = plan.InstanceId,
                operationId = plan.OperationId, journalId = plan.JournalId, extensionId = source.ExtensionId,
                revision = 9007199254740991L, isInstalled = plan.Action != PayloadAction.Remove,
                package = new { extensionId = source.ExtensionId, packageVersion = new string('v', 80),
                    kind = 1, archiveSha256 = reference.Sha256, manifestSha256 = reference.Sha256 },
                targetPath = plan.TargetPath, parentIdentity = plan.ParentIdentity,
                source = reference, physicalResult = reference, payload
            });
            var participants = Enumerable.Range(0, 16).Select(_ => new
            {
                participantId = Guid.Empty, kind = "directory", resourceKey = "extension:" + source.ExtensionId,
                identity = new { format = new string('f', 96), version = 1, beforeRevision = 9007199254740991L,
                    stage = new string('s', 80), artifact = reference }
            }).ToArray();
            var bytes = BoundedJson.Serialize(new
            {
                format = "nexus.execution.journal", version = 1, instanceId = plan.InstanceId, journalId = plan.JournalId,
                operationId = plan.OperationId, operationKind = "extensionApply", phase = "committed",
                revision = 9007199254740991L, planPath = reference.RelativePath, planDigest = reference.Sha256,
                updatedAt = "9999-12-31T23:59:59.9999999Z", resourceKeys = plan.ResourceKeys, participants,
                receipts = new[] { new { receiptId = Guid.Empty, recordedAt = "9999-12-31T23:59:59.9999999Z", stepId = new string('s', 96), effect = "completed",
                    detailCode = new string('d', 128) } }
            });
            if (bytes.Length > StrictJson.MaximumBytes) throw Invalid();
        }
        catch (JsonException failure) { throw new IOException("extension_payload_metadata_capacity", failure); }
    }

    private static IOException Invalid() => new("extension_payload_metadata_capacity");
}
