using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.State;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class SecretDocumentService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ResourceLeaseManager resources, RecoveryIndex recovery, ContractReader contracts, ISecretPreparationObserver? observer = null,
    SecretFieldService? secretService = null)
{
    private readonly SecretOperationFlights _flights = new();

    internal async ValueTask<DocumentCommit> WriteAsync(Guid operationId, string subject, string extensionId, string name,
        long expectedRevision, ReadOnlyMemory<byte> json, IReadOnlyList<SecretDocumentEdit> edits, Action requireCaller, CancellationToken cancellation,
        AcceptedExecutionRun? execution = null, Extensions.ProjectChoicePublication? projectChoice = null)
    {
        lease.VerifyHeld(instance); cancellation.ThrowIfCancellationRequested(); requireCaller();
        using var flight = await _flights.EnterAsync(operationId, cancellation).ConfigureAwait(false);
        var preparation = new SecretDocumentPreparation(instance, database, contracts);
        var replay = Find(operationId);
        var inputs = preparation.Inputs(operationId, subject, extensionId, name, expectedRevision, json, edits, replay, projectChoice);
        if (replay is not null)
        {
            if (inputs.RequestDigest != replay.Document.RequestDigest) throw new OperationConflictException();
            if (new SecretDocumentPublisher(instance, database, contracts).Status(replay) != "completed")
                throw new IOException("secret_document_publication_pending");
            return new(operationId, replay.Document.ExpectedRevision + 1, replay.Document.Content.Sha256, false);
        }
        ResourceClaim[] claims = [new("extension-data:" + extensionId, ResourceClaimMode.Mutation), new("secrets:fields", ResourceClaimMode.Mutation)];
        var admitted = execution is null ? resources.TryAcquire(operationId, claims) : resources.TryContinueRun(execution, operationId, claims);
        using var root = admitted.Lease ?? throw new IOException("secret_document_resource_unavailable");
        using var prepared = preparation.Freeze(inputs, edits, root, resources, cancellation);
        requireCaller(); cancellation.ThrowIfCancellationRequested();
        var publisher = new SecretDocumentPublisher(instance, database, contracts);
        using var cleanup = new SecretRetirementCleanup(instance, database, contracts, resources,
            prepared.Source.Document.Secrets.Where(change => change.Retirement?.Plan is not null)
                .Select(change => new BlobReference(change.Retirement!.Plan!.RelativePath,
                    change.Retirement.Plan.SizeBytes, change.Retirement.Plan.Sha256)));
        secretService?.RequireOwner(instance, database, lease, resources);
        using var authorization = secretService?.BeginDocumentPublication(root, extensionId);
        if (prepared.Payloads.Length == 0)
        {
            using var proof = new PreparedDocumentCommit(instance, database, contracts, prepared.Source);
            var result = database.Write((connection, transaction) =>
            {
                requireCaller(); cancellation.ThrowIfCancellationRequested();
                if (!AcceptInside(connection, transaction, prepared)) throw new OperationConflictException();
                return publisher.CommitInside(connection, transaction, proof, requireCaller);
            });
            foreach (var retirement in proof.Retirements) retirement.Materialize(database);
            cleanup.Finish(instance, database, contracts, lease, recovery);
            PublishRecovery(); return result;
        }
        var blocked = recovery.BlockUnknown("secret_document_publication_in_progress");
        try
        {
            authorization?.BindRecovery(blocked);
            _ = database.Write((connection, transaction) =>
            {
                requireCaller(); cancellation.ThrowIfCancellationRequested();
                if (!AcceptInside(connection, transaction, prepared)) throw new OperationConflictException();
                return true;
            });
            foreach (var payload in prepared.Payloads)
            {
                requireCaller(); cancellation.ThrowIfCancellationRequested();
                observer?.Reached(SecretPreparationPoint.Accepted, Guid.Parse(payload.Plan.ReadWire().JournalId));
                _ = new SecretPreparationExecutor(instance, contracts, database, observer).WriteAcceptedCandidate(payload);
            }
            var result = publisher.Close(prepared.Source, () => { requireCaller(); cancellation.ThrowIfCancellationRequested(); });
            cleanup.Finish(instance, database, contracts, lease, recovery);
            return result;
        }
        finally { PublishRecovery(); }
    }

    internal DocumentCommit? ReadCommit(Guid operation, string extensionId, string subject)
    {
        lease.VerifyHeld(instance);
        var source = Find(operation);
        if (source is null || source.Document.ExtensionId != extensionId || source.Document.SubjectId != subject) return null;
        if (new SecretDocumentPublisher(instance, database, contracts).Status(source) != "completed") return null;
        return new(operation, source.Document.ExpectedRevision + 1, source.Document.Content.Sha256, false);
    }

    private BoundSecretDocument? Find(Guid operation)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var reference = SecretDocumentRows.Find(connection, null, operation);
        return reference is null ? null : new SecretDocumentFormat(instance, contracts).Read(reference);
    }

    private bool AcceptInside(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, PreparedSecretDocument prepared)
    {
        if (prepared.Source.Document.ProjectChoice is { } project)
            Extensions.ProjectChoicePublisher.Before(instance, database, contracts, connection, transaction, project);
        if (!SecretDocumentRows.Accept(connection, transaction, prepared.Source)) return false;
        var document = prepared.Source.Document;
        foreach (var change in document.Secrets)
        {
            var operation = SecretFieldStore.Identity(change.OperationId, new(document.ExtensionId, change.SubjectId, change.Field), change.Before.Revision, change.Disposition);
            var payload = prepared.Payloads.SingleOrDefault(value => value.Plan.ReadWire().OperationId == change.OperationId.ToString("D"));
            if (payload is not null)
            {
                var plan = new SecretPreparationAcceptance(database).RequirePrepared(operation, payload);
                if (plan.ReplayProof != change.ReplayProof || plan.Candidate.SecretId != change.After.SecretId?.ToString("D") ||
                    !SecretPreparationAcceptance.AcceptInside(connection, transaction, operation, plan, payload.Reference, JournalWriterStore.CurrentWriter))
                    throw SecretDocumentFormat.Invalid();
            }
            else if (change.Disposition == SecretDisposition.Set || !OperationRows.Accept(connection, transaction, operation, DateTimeOffset.UtcNow).IsNew)
                throw SecretDocumentFormat.Invalid();
        }
        return true;
    }

    private void PublishRecovery()
    {
        try { new RecoveryCoordinator(new RecoveryJournalRegistry(database), new JournalRecoveryEvidence(instance, new(instance, contracts), database), recovery).VerifyAndPublish(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        { recovery.BlockUnknown("secret_document_reconciliation_required"); }
    }
}
