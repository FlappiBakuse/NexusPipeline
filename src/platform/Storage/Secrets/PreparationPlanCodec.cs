using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
public sealed class PreparationPlanCodec(RuntimeInstance instance, ContractReader contracts, StateDatabase database)
{
    public PreparedSecretPayload Freeze(OperationIdentity operation, Guid journalId, SecretFieldKey key,
        long expectedRevision, SecretValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.RequireStandaloneMutation();
        return FreezeCore(operation, journalId, key, expectedRevision, value, null, cancellationToken);
    }

    internal PreparedSecretPayload FreezeNotification(PreparedSecretBatch batch, NotificationSecretField field,
        SecretValue value, CancellationToken cancellationToken = default) =>
        batch.Use(instance, field, (operation, key, revision) =>
        {
            var prepared = FreezeCore(operation, Guid.NewGuid(), key, revision, value, batch.Binding, cancellationToken);
            try { batch.RequirePrepared(field, prepared.Plan); return prepared; }
            catch { prepared.Dispose(); throw; }
        });

    internal PreparedSecretPayload FreezeDocument(PreparedDocumentSecrets batch, Guid subjectId, string field,
        SecretValue value, CancellationToken cancellationToken) => batch.Use(instance, subjectId, field, (operation, key, revision) =>
            FreezeCore(operation, Guid.NewGuid(), key, revision, value, batch.Binding, cancellationToken, documentPublication: true));

    private PreparedSecretPayload FreezeCore(OperationIdentity operation, Guid journalId, SecretFieldKey key,
        long expectedRevision, SecretValue value, (Guid ParentOperationId, string ParentRequestDigest)? publication,
        CancellationToken cancellationToken, bool documentPublication = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot || journalId == Guid.Empty ||
            operation != SecretFieldStore.Identity(operation.OperationId, key, expectedRevision, SecretDisposition.Set))
            throw new IOException("secret_preparation_identity_unbound");
        var before = new SecretFieldStore(instance, database).GetStateAsync(key, cancellationToken).GetAwaiter().GetResult();
        if (before.Revision != expectedRevision || expectedRevision >= SecretFieldReference.MaximumRevision)
            throw new InvalidOperationException("secret_revision_conflict");
        var held = new List<IDisposable>();
        SecretFieldRetirement? retirement = null;
        try
        {
            if (before.SecretId is { } previous) retirement = SecretFieldRetirement.Prepare(instance, database, operation.OperationId, previous);
            var payloadParent = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "secrets", "payloads"));
            var receiptParent = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "secrets", "preparations"));
            Directory.CreateDirectory(payloadParent); Directory.CreateDirectory(receiptParent);
            held.Add(StableDirectoryLease.Acquire(payloadParent));
            held.Add(StableDirectoryLease.Acquire(receiptParent));
            var id = Guid.NewGuid();
            var payload = new UserSecretProtector().Protect(instance.InstanceId, id, value);
            var relative = $"secrets/payloads/{id:D}-{payload.Sha256}.bin";
            RequireAbsent(Path.Combine(instance.RuntimeRoot, relative));
            foreach (var stage in new[] { "started", "completed" }) RequireAbsent(Path.Combine(receiptParent, $"{journalId:D}.{stage}.json"));
            string proof;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            {
                held.Add(SecretReplayProof.Hold(instance, connection));
                proof = SecretReplayProof.Compute(instance, connection, operation, value);
            }
            var blobs = new ImmutableBlobStore(instance, contracts);
            var source = publication is null ? null : blobs.PersistContent(payload.Ciphertext, cancellationToken);
            if (source is not null) held.Add(blobs.OpenVerified(source));
            var wire = new SecretPreparationPlan
            {
                Format = "nexus.secret.preparation-plan", Version = 1, InstanceId = instance.InstanceId.ToString("D"),
                OperationId = operation.OperationId.ToString("D"), JournalId = journalId.ToString("D"), OperationKind = "secretPrepare",
                OwnerKind = key.OwnerKind switch
                {
                    FieldOwnerKind.Host => "host", FieldOwnerKind.Extension => "extension",
                    FieldOwnerKind.NotificationTask when publication is not null && key.OwnerInstanceId == instance.InstanceId => "notificationTask",
                    _ => throw new IOException("secret_preparation_owner_unbound")
                },
                NotificationPublication = documentPublication || publication is not { } parent ? null : new()
                {
                    ParentOperationId = parent.ParentOperationId.ToString("D"), ParentRequestDigest = parent.ParentRequestDigest,
                    Ciphertext = new() { RelativePath = source!.RelativePath, SizeBytes = source.SizeBytes, Sha256 = source.Sha256 }
                },
                DocumentPublication = !documentPublication || publication is not { } documentParent ? null : new()
                {
                    ParentOperationId = documentParent.ParentOperationId.ToString("D"), ParentRequestDigest = documentParent.ParentRequestDigest,
                    Ciphertext = new() { RelativePath = source!.RelativePath, SizeBytes = source.SizeBytes, Sha256 = source.Sha256 }
                },
                ExtensionId = key.ExtensionId, SubjectId = key.SubjectId.ToString("D"), Field = key.Field, ExpectedRevision = expectedRevision,
                BeforeSecretId = before.SecretId?.ToString("D"), ReplayProof = proof,
                ResourceKeys = documentPublication ? ["extension-data:" + key.ExtensionId, "secrets:fields"] : ["secrets:fields"],
                Candidate = new() { SecretId = id.ToString("D"), ProtectionScheme = payload.ProtectionScheme, OwnerSid = payload.OwnerSid,
                    RelativePath = relative, SizeBytes = payload.Ciphertext.Length, Sha256 = payload.Sha256 },
                PayloadParentIdentity = Wire(PhysicalObjectIdentity.Read(payloadParent)), ReceiptParentIdentity = Wire(PhysicalObjectIdentity.Read(receiptParent)),
                Retirement = retirement?.Describe() ?? new() { State = "none" }
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(wire);
            var frozen = Decode(bytes);
            var reference = blobs.PersistPlan(bytes);
            held.Add(blobs.OpenVerified(reference));
            return new(frozen, reference, payload, retirement, held.ToArray());
        }
        catch
        {
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            retirement?.Dispose();
            throw;
        }
    }

    public FrozenSecretPreparation Read(BlobReference reference)
    {
        if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json" || reference.SizeBytes is < 1 or > StrictJson.MaximumBytes)
            throw new IOException("secret_preparation_plan_reference_invalid");
        using var input = new ImmutableBlobStore(instance, contracts).OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var frozen = Decode(bytes);
        NotificationSecretBinding.RequireAccepted(instance, database, contracts, frozen, reference);
        SecretDocumentBinding.Accepted(instance, database, contracts, frozen, reference);
        return frozen;
    }

    public FrozenSecretPreparation Decode(ReadOnlyMemory<byte> input)
    {
        if (input.Length is < 1 or > StrictJson.MaximumBytes) throw new JsonException("invalid_json_size");
        var owned = input.ToArray();
        var wire = contracts.Read<SecretPreparationPlan>(owned, "secret-preparation-plan.schema.json");
        if (Guid.Parse(wire.InstanceId) != instance.InstanceId || Guid.Parse(wire.OperationId) == Guid.Empty || Guid.Parse(wire.JournalId) == Guid.Empty ||
            Guid.Parse(wire.SubjectId) == Guid.Empty || wire.BeforeSecretId is { } before && Guid.Parse(before) == Guid.Empty ||
            Guid.Parse(wire.Candidate.SecretId) == Guid.Empty || wire.Candidate.SecretId == wire.BeforeSecretId ||
            wire.Candidate.RelativePath != $"secrets/payloads/{wire.Candidate.SecretId}-{wire.Candidate.Sha256}.bin")
            throw new IOException("secret_preparation_plan_identity_invalid");
        NotificationSecretBinding.RequireStructure(wire);
        SecretDocumentBinding.Structure(wire);
        var expectedResources = wire.DocumentPublication is null ? new[] { "secrets:fields" } :
            new[] { "extension-data:" + wire.ExtensionId, "secrets:fields" };
        if (!wire.ResourceKeys.SequenceEqual(expectedResources)) throw new IOException("secret_preparation_resources_unbound");
        var key = SecretPreparationRows.Key(wire);
        if (key.OwnerKind == FieldOwnerKind.Host && key.SubjectId != instance.InstanceId)
            throw new IOException("secret_host_instance_unbound");
        using var user = WindowsIdentity.GetCurrent();
        if (wire.Candidate.OwnerSid != user.User?.Value) throw new IOException("secret_preparation_owner_unbound");
        if ((wire.BeforeSecretId is null) != (wire.Retirement.State == "none")) throw new IOException("secret_preparation_retirement_unbound");
        if (wire.Retirement.State == "planned") VerifyRetirement(wire);
        return new(owned);
    }

    private void VerifyRetirement(SecretPreparationPlan wire)
    {
        var reference = wire.Retirement.Plan!;
        var plan = new CleanupPlanCodec(instance, contracts, new(instance, contracts))
            .Read(new(reference.RelativePath, reference.SizeBytes, reference.Sha256));
        if (plan.OperationId.ToString("D") != wire.OperationId || plan.JournalId.ToString("D") != wire.Retirement.JournalId ||
            plan.JournalId.ToString("D") == wire.JournalId || plan.Items.Count != 1 || CleanupCandidateFiles.SecretId(plan.Items[0].Blob)?.ToString("D") != wire.BeforeSecretId)
            throw new IOException("secret_preparation_retirement_plan_unbound");
    }

    private static FileTreePhysicalIdentity Wire(PhysicalObjectIdentity identity) => new() { VolumeSerial = identity.VolumeSerial, FileId = identity.FileId };
    private static void RequireAbsent(string path)
    {
        try { _ = File.GetAttributes(PhysicalPathPolicy.NormalizeWithoutLinks(path)); }
        catch (FileNotFoundException) { return; }
        throw new IOException("secret_preparation_candidate_already_exists");
    }
}
