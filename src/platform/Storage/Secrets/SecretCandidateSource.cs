using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal static class SecretCandidateSource
{
    internal static PreparedSecretPayload Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        FrozenSecretPreparation frozen, BlobReference reference)
    {
        var plan = frozen.ReadWire();
        var notification = NotificationSecretBinding.RequireAccepted(instance, database, contracts, frozen, reference);
        var document = SecretDocumentBinding.Accepted(instance, database, contracts, frozen, reference);
        if (notification is null && document is null) throw new IOException("secret_parent_source_authority_missing");
        var source = plan.NotificationPublication?.Ciphertext ?? plan.DocumentPublication?.Ciphertext
            ?? throw new IOException("secret_parent_source_missing");
        var held = new List<IDisposable>();
        SecretFieldRetirement? retirement = null;
        byte[]? bytes = null;
        try
        {
            var stream = new ImmutableBlobStore(instance, contracts).OpenVerified(new(source.RelativePath, source.SizeBytes, source.Sha256));
            held.Add(stream); bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            var candidate = plan.Candidate;
            var payload = new ProtectedSecretPayload(candidate.ProtectionScheme, candidate.OwnerSid, candidate.Sha256, bytes);
            using var value = new UserSecretProtector().Unprotect(instance.InstanceId, Guid.Parse(candidate.SecretId), payload);
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System);
            held.Add(SecretReplayProof.Hold(instance, connection));
            var operation = SecretFieldStore.Identity(Guid.Parse(plan.OperationId), SecretPreparationRows.Key(plan),
                plan.ExpectedRevision, SecretDisposition.Set);
            if (!SecretReplayProof.Matches(plan.ReplayProof, SecretReplayProof.Compute(instance, connection, operation, value)))
                throw new IOException("notification_secret_source_proof_unbound");
            retirement = SecretFieldRetirement.FromFrozen(instance, plan);
            return new(frozen, reference, payload, retirement, held.ToArray());
        }
        catch
        {
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            retirement?.Dispose(); throw;
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
