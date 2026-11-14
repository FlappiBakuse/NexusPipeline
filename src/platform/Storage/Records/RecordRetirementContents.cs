using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

internal sealed class RecordRetirementContents(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid runId,
    RecordRetirementFamily? capturedFamily = null, RetirementContentKind kind = RetirementContentKind.Logs)
{
    private RecordRetirementFamily? _family = RequireCapturedFamily(instance.InstanceId, runId, capturedFamily);
    private readonly Dictionary<BlobReference, IReadOnlyList<BlobReference>> _proofs = [];

    internal IReadOnlySet<BlobReference> Read(IReadOnlyList<BlobReference> candidates)
    {
        IReadOnlyList<BlobReference> proofs;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            proofs = RecordRetirementRows.ProofsFor(connection, transaction, runId, candidates);
            transaction.Commit();
        }
        var wanted = candidates.ToHashSet();
        var result = new HashSet<BlobReference>();
        foreach (var reference in proofs)
        {
            if (!_proofs.TryGetValue(reference, out var saved))
            {
                _family ??= new RetirementFamilyReader(instance, database, contracts).CaptureMetadata(runId);
                var proof = VerifiedRecordRetirement.ReadProof(instance, database, contracts, _family, reference);
                saved = proof.ContentKind == kind ? proof.Contents : [];
                if (_proofs.Count == 32) _proofs.Clear();
                _proofs.Add(reference, saved);
            }
            foreach (var content in saved) if (wanted.Contains(content)) result.Add(content);
        }
        result.IntersectWith(candidates);
        return result;
    }

    internal void RequireAvailable(IReadOnlyList<BlobReference> contents)
    {
        if (Read(contents).Count != 0) throw new RetiredContentException();
    }

    private static RecordRetirementFamily? RequireCapturedFamily(Guid instanceId, Guid runId, RecordRetirementFamily? family)
    {
        if (family is null) return null;
        RecordRetirementCodec.Validate(family, instanceId);
        if (family.RunId != runId) throw RecordRetirementCodec.Invalid();
        return family;
    }

    internal static IReadOnlyList<BlobReference> RetainedReferences(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, StepEvidenceBinding binding, string role, BlobReference artifact,
        IReadOnlyList<BlobReference> references)
    {
        var authority = VerifiedRecordRetirement.ForArtifact(instance, database, contracts, binding, role, artifact);
        return authority is null ? references : references.Where(item => !authority.Contains(item)).ToArray();
    }
}
