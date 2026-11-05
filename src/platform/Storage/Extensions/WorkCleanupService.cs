using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class WorkCleanupService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources, RecoveryIndex recovery,
    ExtensionActivator activator)
{
    internal const int PageNodeCount = 64;

    internal async ValueTask<CleanupAcceptanceResult> RunPageAsync(Guid operationId, string subject,
        Guid producerJournalId, BlobReference producerPlan, Guid? previousJournalId, BlobReference? previousPlan,
        CancellationToken acceptedCancellation, ICleanupPersistenceObserver? observer = null)
    {
        lease.VerifyHeld(instance); lease.RequireExtensionActivator(activator, resources);
        if (operationId == Guid.Empty || subject is not { Length: >= 1 and <= 256 } || subject.Any(char.IsControl) ||
            (previousJournalId is null) != (previousPlan is null) || !resources.UsesRecoveryIndex(recovery)) throw Invalid();
        var identity = ExtensionWorkRequest.Identity(instance, operationId, subject, producerJournalId, producerPlan, previousJournalId, previousPlan);
        var existing = ReadAccepted(identity);
        if (existing is not null) return existing;
        var codec = new ExtensionWorkCodec(instance, database, contracts, roots);
        var origin = CreateProof(producerJournalId, producerPlan, codec);
        var completed = new ExtensionWorkHistory(instance, database, contracts, roots).Completed(origin, previousJournalId, previousPlan);
        var indices = origin.Proof.Nodes.Where(node => !completed.Contains(node.Index)).Take(PageNodeCount).Select(node => node.Index).ToImmutableArray();
        if (indices.IsEmpty) throw new IOException("extension_work_already_removed");
        var acquired = resources.TryAcquire(operationId, ExtensionWorkClaims.Read(origin));
        using var root = acquired.Lease ?? throw new IOException("extension_work_cleanup_resource_unavailable");
        var journalId = Guid.NewGuid();
        var page = new ExtensionWorkPage(ExtensionWorkCodec.PageFormat, 1, instance.InstanceId, operationId,
            journalId, origin.Reference, previousJournalId, previousPlan, indices);
        var pageReference = codec.PersistPage(page);
        var candidate = new FrozenFileCleanup(instance.InstanceId, operationId, journalId, [])
        { ExtensionWork = codec.ReadPage(pageReference) };
        using var prepared = await PreparedWorkCleanup.PrepareAsync(instance, database, lease, contracts, roots,
            resources, activator, root, candidate, subject, isNew: true, acceptedCancellation).ConfigureAwait(false);
        var accepted = new FileCleanupAcceptance(instance, contracts, database, observer, roots)
            .AcceptPrepared(identity, prepared, DateTimeOffset.UtcNow);
        _ = new OwnedCleanupExecutor(instance, contracts, database, recovery).ExecutePrepared(
            accepted.JournalId, lease, root, roots, prepared.Execution, observer);
        return accepted;
    }

    private CleanupAcceptanceResult? ReadAccepted(OperationIdentity identity)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var acceptance = OperationRows.ReadExisting(connection, transaction, identity);
        if (acceptance is null) return null;
        var operation = OperationRows.ReadOwned(connection, transaction, identity.OperationId, identity.SubjectId) ?? throw Invalid();
        if (operation.InitialJournalId is not { } id) throw Invalid();
        var registered = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, id);
        transaction.Commit();
        var plan = CleanupJournalBinding.ReadPlan(instance, contracts, registered, database, roots);
        if (plan.ExtensionWork is not { } work || identity != ExtensionWorkRequest.Identity(instance, identity.OperationId,
            identity.SubjectId, work.Origin.Proof.ProducerJournalId, work.Origin.Proof.ProducerPlan,
            work.Page.PreviousJournalId, work.Page.PreviousPlan)) throw Invalid();
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, registered.PlanPath));
        return new(acceptance, id, new(registered.PlanPath, new FileInfo(path).Length, registered.PlanDigest));
    }

    private BoundExtensionWork CreateProof(Guid journalId, BlobReference reference, ExtensionWorkCodec codec)
    {
        var bound = new ExtensionPayloadBinding(instance, database, contracts, roots).Read(journalId, reference);
        var last = bound.Physical.LastOrDefault()?.Record ?? throw Invalid();
        var terminal = bound.CommitIntent?.Artifact ?? (!bound.Restoration.IsEmpty ? bound.Restoration[^1].Intent.Artifact : null) ?? throw Invalid();
        var layout = bound.Commit is not null ? bound.Plan.Before.Snapshot.Shape == FileObjectKind.Directory ? "before" : "empty" :
            last.CandidateObjects.IsEmpty ? "empty" : bound.Restoration[0].Record.RetireCandidate ? "spent" : "candidate";
        var proof = new ExtensionWorkProof(ExtensionWorkCodec.ProofFormat, 1, instance.InstanceId, bound.Plan.OperationId,
            journalId, reference, bound.Latest.Revision, Convert.ToHexStringLower(SHA256.HashData(bound.Latest.Export())),
            terminal, layout, ExtensionWorkspacePath.For(bound.Plan), bound.Plan.ParentIdentity,
            last.WorkspaceIdentity ?? throw Invalid(), WorkNodeDerivation.Derive(bound, layout));
        return codec.ReadProof(codec.PersistProof(proof));
    }

    private static IOException Invalid() => new("extension_work_cleanup_request_unbound");
}
