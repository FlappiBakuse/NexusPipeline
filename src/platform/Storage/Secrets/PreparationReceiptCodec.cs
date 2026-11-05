using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Secrets;

public sealed class PreparationReceiptCodec(ContractReader contracts)
{
    public byte[] Encode(FrozenSecretPreparation frozen, BlobReference planReference, Guid writerId, string stage, PhysicalObjectIdentity? identity)
    {
        var plan = frozen.ReadWire();
        var receipt = new SecretPreparationReceipt
        {
            Format = "nexus.secret.preparation.receipt", Version = 1, InstanceId = plan.InstanceId, OperationId = plan.OperationId,
            JournalId = plan.JournalId, WriterId = writerId.ToString("D"), PlanDigest = planReference.Sha256,
            Stage = stage, Candidate = plan.Candidate, ParentIdentity = plan.PayloadParentIdentity,
            Identity = identity is null ? null : new() { VolumeSerial = identity.VolumeSerial, FileId = identity.FileId }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
        _ = Decode(frozen, planReference, bytes);
        return bytes;
    }

    public SecretPreparationReceipt Decode(FrozenSecretPreparation frozen, BlobReference reference, ReadOnlyMemory<byte> input)
    {
        if (input.Length is < 1 or > StrictJson.MaximumBytes) throw new JsonException("invalid_json_size");
        var receipt = contracts.Read<SecretPreparationReceipt>(input.ToArray(), "secret-preparation-receipt.schema.json");
        var plan = frozen.ReadWire();
        var original = frozen.Export();
        if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json" || reference.SizeBytes != original.Length ||
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(original)) != reference.Sha256 ||
            receipt.PlanDigest != reference.Sha256 || receipt.InstanceId != plan.InstanceId || receipt.OperationId != plan.OperationId ||
            receipt.JournalId != plan.JournalId || Guid.Parse(receipt.WriterId) == Guid.Empty || receipt.Candidate != plan.Candidate ||
            receipt.ParentIdentity != plan.PayloadParentIdentity ||
            receipt.Identity is { } identity && identity.VolumeSerial != plan.PayloadParentIdentity.VolumeSerial)
            throw new IOException("secret_preparation_receipt_unbound");
        return receipt;
    }
}
