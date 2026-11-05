using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Maintenance;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed record PayloadAcceptanceResult(OperationAcceptance Acceptance, Guid JournalId, BlobReference Plan);

public sealed class PayloadApplyAcceptance(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    PayloadApplyRoots roots, IPayloadPersistenceObserver? observer = null)
{
    public PayloadAcceptanceResult Accept(OperationIdentity operation, FrozenPayloadApply candidate, DateTimeOffset acceptedAt)
    {
        if (candidate.Kind == JournalKind.ExtensionApply) throw new IOException("extension_apply_preparation_required");
        return AcceptCore(operation, candidate, acceptedAt);
    }

    [SupportedOSPlatform("windows")]
    internal PayloadAcceptanceResult AcceptPrepared(PreparedExtensionPayload candidate, DateTimeOffset acceptedAt) =>
        candidate.UseAcceptance(instance, database, roots, (identity, plan, source) => AcceptCore(identity, plan, acceptedAt, source));

    private PayloadAcceptanceResult AcceptCore(OperationIdentity operation, FrozenPayloadApply candidate,
        DateTimeOffset acceptedAt, ExtensionPayloadSource? source = null)
    {
        if (database.InstanceId != instance.InstanceId || operation.OperationId != candidate.OperationId)
            throw new IOException("payload_acceptance_identity_unbound");
        var codec = new PayloadPlanCodec(instance, contracts, new(instance, contracts), roots);
        using var prepared = codec.Freeze(candidate);
        var plan = prepared.Plan;
        var registered = new RegisteredJournal(plan.JournalId, plan.OperationId, plan.Kind, JournalPhase.Prepared, 1,
            prepared.Reference.RelativePath, prepared.Reference.Sha256, Array.AsReadOnly(plan.ResourceKeys.Order(StringComparer.Ordinal).ToArray()));
        var effects = PayloadFileIntents.Create(plan, prepared.Reference.Sha256);
        var result = source is null
            ? database.Write((connection, transaction) =>
            {
                var update = new UpdateSourceCodec(instance, contracts);
                var origin = update.ReadMetadata(plan.MaintenanceSource ?? throw new IOException("maintenance_update_source_required"));
                update.RequireFrozenBinding(origin, plan);
                update.RequireAcceptedInside(connection, transaction, origin);
                return RecoveryJournalRegistry.AcceptPreparedInside(connection, transaction, operation, registered,
                    effects, acceptedAt, null, JournalWriterStore.CurrentWriter);
            })
            : database.Write((connection, transaction) =>
            {
                if (operation.Kind != "extension.apply" || operation.RequestDigest !=
                    ExtensionApplyRequest.FromSource(source).Identity(operation.OperationId, operation.SubjectId).RequestDigest ||
                    plan.ExtensionSource is null || source.OperationId != operation.OperationId ||
                    source.JournalId != registered.JournalId) throw new IOException("extension_apply_preparation_unbound");
                if (OperationRows.ReadExisting(connection, transaction, operation) is null)
                    PreparedExtensionPayload.RequireCurrentInside(connection, transaction, source);
                return RecoveryJournalRegistry.AcceptPreparedInside(connection, transaction, operation, registered,
                    effects, acceptedAt, source.Claims, JournalWriterStore.CurrentWriter);
            });
        if (!result.Acceptance.IsNew)
        {
            var original = new RecoveryJournalRegistry(database).ReadJournal(result.JournalId);
            var full = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, original.PlanPath));
            var reference = new BlobReference(original.PlanPath, new FileInfo(full).Length, original.PlanDigest);
            _ = codec.Read(reference);
            return new(result.Acceptance, original.JournalId, reference);
        }
        observer?.Reached(PayloadPersistencePoint.AcceptanceCommitted, registered.JournalId, 1, JournalPhase.Prepared);
        MaterializeInitial(registered);
        return new(result.Acceptance, registered.JournalId, prepared.Reference);
    }

    internal void MaterializeInitial(RegisteredJournal journal)
    {
        var document = new ExecutionJournal
        {
            Format = "nexus.execution.journal", Version = 1, InstanceId = instance.InstanceId.ToString("D"),
            JournalId = journal.JournalId.ToString("D"), OperationId = journal.OperationId.ToString("D"),
            OperationKind = journal.Kind == JournalKind.UpdateApply ? "updateApply" : "extensionApply",
            Phase = "prepared", Revision = 1, PlanPath = journal.PlanPath, PlanDigest = journal.PlanDigest,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"), ResourceKeys = journal.ResourceKeys,
            Participants = [], Receipts = []
        };
        new JournalStore(instance, contracts).Create(JsonSerializer.SerializeToUtf8Bytes(document));
        observer?.Reached(PayloadPersistencePoint.JournalCreated, journal.JournalId, 1, JournalPhase.Prepared);
    }
}
