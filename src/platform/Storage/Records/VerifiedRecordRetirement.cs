using System.Security.Cryptography;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

internal sealed class VerifiedRecordRetirement
{
    private readonly ImmutableBlobStore _blobs;
    private readonly RecordRetirementArtifact _artifact;
    private readonly HashSet<BlobReference> _retired;

    private VerifiedRecordRetirement(ImmutableBlobStore blobs, RecordRetirementArtifact artifact, HashSet<BlobReference> retired)
        { _blobs = blobs; _artifact = artifact; _retired = retired; }

    internal static VerifiedRecordRetirement? ForArtifact(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        StepEvidenceBinding binding, string role, BlobReference artifact)
    {
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = RecordRetirementRows.Command(connection, null,
            "SELECT EXISTS(SELECT 1 FROM record_content_retirements WHERE run_id=$0);", binding.RunId))
            if ((long)command.ExecuteScalar()! == 0) return null;
        var reader = new RetirementFamilyReader(instance, database, contracts);
        var family = reader.CaptureMetadata(binding.RunId);
        var selected = family.Attempts.SelectMany(item => item.Artifacts)
            .SingleOrDefault(item => item.Binding == binding && item.Role == role && item.Artifact == artifact);
        if (selected is null) return null;
        var blobs = new ImmutableBlobStore(instance, contracts);
        var parts = selected.RawOutputs.SelectMany(reference =>
            RawOutputCodec.Read(blobs, binding, reference).Parts.Select(item => item.Content)).Distinct()
            .Take(RawOutputCodec.MaximumParts + 1).ToArray();
        if (parts.Length > RawOutputCodec.MaximumParts) throw new IOException("record_retirement_metadata_limit");
        var retired = ReadCovered(instance, database, contracts, family, parts);
        return retired.Count == 0 ? null : new(blobs, selected, retired);
    }

    internal static HashSet<BlobReference> ReadCovered(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RecordRetirementFamily family, IReadOnlyList<BlobReference> candidates)
    {
        var blobs = new ImmutableBlobStore(instance, contracts);
        IReadOnlyList<BlobReference> proofs;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            RecordRetirementRows.RequireFamilyInside(connection, transaction, family.RunId, family.Attempts.Length);
            proofs = RecordRetirementRows.ProofsFor(connection, transaction, family.RunId, candidates);
            transaction.Commit();
        }
        var wanted = candidates.ToHashSet();
        var result = new HashSet<BlobReference>();
        foreach (var reference in proofs)
        {
            var document = ReadProof(instance, database, contracts, family, reference);
            if (document.ContentKind == RetirementContentKind.Logs)
                foreach (var content in document.Contents) if (wanted.Contains(content)) result.Add(content);
        }
        result.IntersectWith(candidates);
        return result;
    }

    internal static RecordRetirementDocument ReadProof(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RecordRetirementFamily currentFamily, BlobReference reference)
    {
        var blobs = new ImmutableBlobStore(instance, contracts);
        var document = RecordRetirementCodec.Read<RecordRetirementDocument>(blobs, reference);
        RecordRetirementCodec.Validate(document, instance.InstanceId);
        var family = RecordRetirementCodec.Read<RecordRetirementFamily>(blobs, document.Family);
        RecordRetirementCodec.Validate(family, instance.InstanceId);
        if (document.RunId != currentFamily.RunId || family.RunId != currentFamily.RunId ||
            Hash(family) != document.Family.Sha256 || Hash(currentFamily) != document.Family.Sha256) throw RecordRetirementCodec.Invalid();
        var cleanup = new CleanupPlanCodec(instance, contracts, blobs).Read(document.Cleanup);
        if (cleanup.InstanceId != instance.InstanceId || cleanup.OperationId != document.OperationId ||
            cleanup.JournalId != document.CleanupJournalId || !cleanup.Items.Select(item => item.Blob).SequenceEqual(document.Contents))
            throw RecordRetirementCodec.Invalid();
        var unbound = document.Contents.ToHashSet();
        foreach (var item in new RetirementFamilyReader(instance, database, contracts).Candidates(family, document.ContentKind))
        {
            unbound.Remove(item);
            if (unbound.Count == 0) break;
        }
        if (unbound.Count != 0) throw RecordRetirementCodec.Invalid();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        _ = RecoveryJournalRegistry.ReadValidatedSnapshotInside(connection, transaction);
        RecordRetirementRows.RequireSavedInside(connection, transaction, document, reference);
        RecordRetirementRows.RequireFamilyInside(connection, transaction, family.RunId, family.Attempts.Length);
        foreach (var item in family.Attempts)
        {
            if (AttemptReceiptStore.ReadInside(connection, transaction, item.Receipt.Facts.AttemptId) != item.Receipt)
                throw RecordRetirementCodec.Invalid();
            RecordRetirementRows.RequireWritersInside(connection, transaction, item);
            if (document.ContentKind == RetirementContentKind.Images)
                RetirementImageEvidence.RequireCatalogInside(connection, transaction, item);
            var registered = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, item.Receipt.JournalId);
            if (registered.Revision != item.Revision || registered.Phase != NexusPipeline.Core.Configuration.JournalPhase.Committed)
                throw RecordRetirementCodec.Invalid();
        }
        var registeredCleanup = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, cleanup.JournalId);
        if (registeredCleanup.Kind != NexusPipeline.Core.Configuration.JournalKind.FileCleanup ||
            registeredCleanup.OperationId != cleanup.OperationId || registeredCleanup.PlanPath != document.Cleanup.RelativePath ||
            registeredCleanup.PlanDigest != document.Cleanup.Sha256 ||
            !registeredCleanup.ResourceKeys.SequenceEqual(cleanup.Items.Select(item => CleanupCandidateFiles.ResourceKey(item.Blob)).Order(StringComparer.Ordinal))) throw RecordRetirementCodec.Invalid();
        transaction.Commit(); return document;
    }

    internal bool Contains(BlobReference content) => _retired.Contains(content);

    internal bool ReplacesRawReplay(StepEvidenceBinding binding, BlobReference reference)
    {
        if (binding != _artifact.Binding || !_artifact.RawOutputs.Contains(reference)) throw RecordRetirementCodec.Invalid();
        var raw = RawOutputCodec.Read(_blobs, binding, reference);
        var replaces = false;
        foreach (var part in raw.Parts)
        {
            if (_retired.Contains(part.Content)) replaces = true;
            else _blobs.Verify(part.Content);
        }
        return replaces;
    }

    internal void RequireOriginalJudgement(BlobReference reference, ExecutionJudgementEvidence value)
    {
        var documents = new Queue<(BlobReference Reference, int Depth)>(); documents.Enqueue((_artifact.Artifact, 0));
        while (documents.TryDequeue(out var next))
        {
            if (next.Depth > 2) throw RecordRetirementCodec.Invalid();
            var document = StepEvidenceCodec.Read(_blobs, _artifact.Binding, next.Reference);
            if (document.Judgement == reference)
            {
                if (value.Identity.Primary != _artifact.Binding) throw RecordRetirementCodec.Invalid();
                foreach (var item in value.Exchanges)
                    if (!_artifact.RawOutputs.Contains(item.RawOutput)) throw RecordRetirementCodec.Invalid();
                return;
            }
            if (document.PreviousObservation is { } previous) documents.Enqueue((previous, next.Depth + 1));
        }
        throw RecordRetirementCodec.Invalid();
    }

    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(value, RecordRetirementCodec.Options)));
}
