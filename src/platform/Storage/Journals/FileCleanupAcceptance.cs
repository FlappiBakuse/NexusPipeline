using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed record CleanupAcceptanceResult(OperationAcceptance Acceptance, Guid JournalId, BlobReference Plan);

public sealed class FileCleanupAcceptance(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    ICleanupPersistenceObserver? observer = null, PayloadApplyRoots? payloadRoots = null)
{
    public CleanupAcceptanceResult Accept(OperationIdentity operation, FrozenFileCleanup candidate, DateTimeOffset acceptedAt)
    {
        if (candidate.ExtensionWork is not null) throw new IOException("extension_work_prepared_authority_required");
        return AcceptCore(operation, candidate, acceptedAt);
    }

    [SupportedOSPlatform("windows")]
    internal CleanupAcceptanceResult AcceptPrepared(OperationIdentity operation, PreparedWorkCleanup prepared,
        DateTimeOffset acceptedAt) => prepared.Publish(candidate => operation == prepared.Identity ? AcceptCore(operation, candidate, acceptedAt, prepared.Execution) :
            throw new IOException("extension_work_acceptance_identity_unbound"));

    private CleanupAcceptanceResult AcceptCore(OperationIdentity operation, FrozenFileCleanup candidate, DateTimeOffset acceptedAt,
        ExtensionWorkExecution? work = null)
    {
        if (operation.OperationId != candidate.OperationId || database.InstanceId != instance.InstanceId)
            throw new IOException("cleanup_acceptance_identity_unbound");
        var blobs = new ImmutableBlobStore(instance, contracts);
        var codec = new CleanupPlanCodec(instance, contracts, blobs, database, payloadRoots);
        var reference = codec.Persist(candidate);
        work?.RequirePlanReference(reference);
        using var held = blobs.OpenVerified(reference);
        var plan = codec.Read(reference);
        var registered = new RegisteredJournal(plan.JournalId, plan.OperationId, JournalKind.FileCleanup, JournalPhase.Prepared, 1,
            reference.RelativePath, reference.Sha256, FileCleanupItems.Keys(plan));
        var effects = FileCleanupItems.Steps(plan).Select(step => new FileEffect(plan.JournalId, step, FileEffectAction.RemoveOwned,
            FileEffectState.Pending, reference.Sha256, null)).ToArray();
        var result = work is null ? new RecoveryJournalRegistry(database).AcceptPrepared(operation, registered, effects, acceptedAt) :
            database.Write((connection, transaction) =>
            {
                work.RequireInside(connection, transaction, allowUnaccepted: true);
                return RecoveryJournalRegistry.AcceptPreparedInside(connection, transaction, operation, registered, effects,
                    acceptedAt, FileCleanupItems.Claims(plan), JournalWriterStore.CurrentWriter);
            });
        if (!result.Acceptance.IsNew)
        {
            var original = new RecoveryJournalRegistry(database).ReadJournal(result.JournalId);
            var path = Path.Combine(instance.RuntimeRoot, original.PlanPath);
            return new(result.Acceptance, original.JournalId, new(original.PlanPath, new FileInfo(path).Length, original.PlanDigest));
        }
        observer?.Reached(CleanupPersistencePoint.AcceptanceCommitted, registered.JournalId, 1, JournalPhase.Prepared);
        MaterializeInitial(registered);
        return new(result.Acceptance, result.JournalId, reference);
    }

    internal void MaterializeInitial(RegisteredJournal journal)
    {
        var document = new ExecutionJournal
        {
            Format = "nexus.execution.journal", Version = 1, InstanceId = instance.InstanceId.ToString("D"),
            JournalId = journal.JournalId.ToString("D"), OperationId = journal.OperationId.ToString("D"), OperationKind = "fileCleanup",
            Phase = "prepared", Revision = 1, PlanPath = journal.PlanPath, PlanDigest = journal.PlanDigest,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"), ResourceKeys = journal.ResourceKeys,
            Participants = [], Receipts = []
        };
        new JournalStore(instance, contracts).Create(JsonSerializer.SerializeToUtf8Bytes(document));
    }
}

