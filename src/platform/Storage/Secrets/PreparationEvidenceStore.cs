using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed record SecretReceiptEvidence(SecretPreparationReceipt Receipt, string Digest);
internal sealed class SecretPreparationEvidence(List<IDisposable> held) : IDisposable
{
    internal SecretReceiptEvidence? Started { get; set; }
    internal SecretReceiptEvidence? Completed { get; set; }
    internal bool CandidateVerified { get; set; }
    internal bool NoEffects { get; set; }
    public void Dispose() { for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose(); }
}

[SupportedOSPlatform("windows")]
internal sealed class PreparationEvidenceStore(RuntimeInstance instance, ContractReader contracts, StateDatabase database)
{
    internal SecretReceiptEvidence Write(FrozenSecretPreparation frozen, BlobReference reference, string stage, PhysicalObjectIdentity? identity)
    {
        var plan = frozen.ReadWire(); var id = Guid.Parse(plan.JournalId);
        var registered = new RecoveryJournalRegistry(database).ReadJournal(id);
        if (registered.Kind != JournalKind.SecretPrepare || registered.Phase != JournalPhase.Applying ||
            new JournalEffectGate(database).Read(id).State != EffectGateState.Entered) throw new IOException("secret_receipt_effect_not_entered");
        var writer = new JournalWriterStore(database).RequireCurrentExecutor(id);
        VerifyParent(plan, receipts: true);
        var bytes = new PreparationReceiptCodec(contracts).Encode(frozen, reference, writer, stage, identity);
        if (stage == "completed" && !File.Exists(PathFor(plan, "started"))) throw new IOException("secret_receipt_started_missing");
        using (var output = new FileStream(PathFor(plan, stage), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(bytes); output.Flush(flushToDisk: true); }
        return new(new PreparationReceiptCodec(contracts).Decode(frozen, reference, bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    internal SecretPreparationEvidence Read(FrozenSecretPreparation frozen, BlobReference reference, bool inspectCandidate)
    {
        var plan = frozen.ReadWire(); var held = new List<IDisposable>();
        var result = new SecretPreparationEvidence(held);
        try
        {
            if (inspectCandidate) VerifyParent(plan, receipts: true);
            if (Directory.Exists(ReceiptParent)) held.Add(new PhysicalDirectoryLease(ReceiptParent,
                inspectCandidate ? Physical(plan.ReceiptParentIdentity) : PhysicalObjectIdentity.Read(ReceiptParent)));
            else if (inspectCandidate || File.Exists(ReceiptParent)) throw new IOException("secret_receipt_parent_shape_invalid");
            result.Started = ReadReceipt("started"); result.Completed = ReadReceipt("completed");
            if (result.Completed is not null && (result.Started is null || result.Completed.Receipt.WriterId != result.Started.Receipt.WriterId))
                throw new IOException("secret_completed_without_matching_started");
            if (inspectCandidate)
            {
                VerifyParent(plan, receipts: false);
                held.Add(new PhysicalDirectoryLease(PayloadParent, Physical(plan.PayloadParentIdentity)));
                result.NoEffects = result.Started is null && result.Completed is null && IsAbsent(CandidatePath(plan));
                if (result.Completed is not null) result.CandidateVerified = VerifyCandidate(plan, reference, result.Completed.Receipt, held);
            }
            return result;
        }
        catch { result.Dispose(); throw; }

        SecretReceiptEvidence? ReadReceipt(string stage)
        {
            var path = PathFor(plan, stage);
            if (IsAbsent(path)) return null;
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); held.Add(stream);
            if (stream.Length is < 1 or > 8192) throw new IOException("secret_receipt_size_invalid");
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            var receipt = new PreparationReceiptCodec(contracts).Decode(frozen, reference, bytes);
            if (receipt.Stage != stage || !new JournalWriterStore(database).IsRegistered(Guid.Parse(plan.JournalId), Guid.Parse(receipt.WriterId)))
                throw new IOException("secret_receipt_writer_unbound");
            return new(receipt, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    private bool VerifyCandidate(SecretPreparationPlan plan, BlobReference reference, SecretPreparationReceipt completed, List<IDisposable> held)
    {
        try
        {
            var stream = new FileStream(CandidatePath(plan), FileMode.Open, FileAccess.Read, FileShare.Read);
            held.Add(stream);
            var candidate = plan.Candidate;
            if (stream.Length != candidate.SizeBytes || PhysicalObjectIdentity.Read(stream.SafeFileHandle) != Physical(completed.Identity!) ||
                Convert.ToHexStringLower(SHA256.HashData(stream)) != candidate.Sha256) return false;
            stream.Position = 0; var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            using var value = new UserSecretProtector().Unprotect(instance.InstanceId, Guid.Parse(candidate.SecretId),
                new(candidate.ProtectionScheme, candidate.OwnerSid, candidate.Sha256, bytes));
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            var operation = SecretFieldStore.Identity(Guid.Parse(plan.OperationId), SecretPreparationRows.Key(plan), plan.ExpectedRevision, SecretDisposition.Set);
            return SecretReplayProof.Matches(plan.ReplayProof, SecretReplayProof.Compute(instance, connection, operation, value));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    internal void VerifyParent(SecretPreparationPlan plan, bool receipts)
    {
        var path = receipts ? ReceiptParent : PayloadParent;
        if (PhysicalObjectIdentity.Read(PhysicalPathPolicy.NormalizeWithoutLinks(path)) != Physical(receipts ? plan.ReceiptParentIdentity : plan.PayloadParentIdentity))
            throw new IOException("secret_preparation_parent_replaced");
    }

    internal bool StillNoEffects(SecretPreparationPlan plan)
    {
        VerifyParent(plan, receipts: true); VerifyParent(plan, receipts: false);
        return IsAbsent(CandidatePath(plan)) && IsAbsent(PathFor(plan, "started")) && IsAbsent(PathFor(plan, "completed"));
    }

    internal static void VerifyInventory(RuntimeInstance instance, StateDatabase database)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "secrets", "preparations"));
        if (!Directory.Exists(path)) { if (File.Exists(path)) throw new IOException("secret_receipt_parent_shape_invalid"); return; }
        var known = new RecoveryJournalRegistry(database).ReadHistory().Where(value => value.Kind == JournalKind.SecretPrepare)
            .SelectMany(value => new[] { $"{value.JournalId:D}.started.json", $"{value.JournalId:D}.completed.json" }).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            if (!known.Contains(Path.GetFileName(entry)) || !File.Exists(PhysicalPathPolicy.NormalizeWithoutLinks(entry)))
                throw new IOException("secret_receipt_inventory_unrecognized");
    }

    internal string CandidatePath(SecretPreparationPlan plan) => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, plan.Candidate.RelativePath));
    private string ReceiptParent => Path.Combine(instance.RuntimeRoot, "secrets", "preparations");
    private string PayloadParent => Path.Combine(instance.RuntimeRoot, "secrets", "payloads");
    private string PathFor(SecretPreparationPlan plan, string stage) => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(ReceiptParent, $"{plan.JournalId}.{stage}.json"));
    internal static PhysicalObjectIdentity Physical(FileTreePhysicalIdentity value) => new(value.VolumeSerial, value.FileId);
    internal static bool IsAbsent(string path)
    {
        try { _ = File.GetAttributes(PhysicalPathPolicy.NormalizeWithoutLinks(path)); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }
}
