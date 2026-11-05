using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record BoundMaintenancePayload(FrozenPayloadApply Plan, BlobReference PlanReference,
    HostUpdateSource Source, RegisteredJournal Registered, MaintenanceOperationRow Operation,
    ImmutableArray<JournalDocument> History, ImmutableArray<MaintenancePhysicalEntry> Physical,
    ImmutableArray<MaintenanceRestorationEntry> Restoration, MaintenanceCommitEntry? Commit = null)
{
    internal JournalDocument Latest => History[^1];
}

[SupportedOSPlatform("windows")]
internal sealed class MaintenancePayloadBinding(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots, UpdateFeedVerifier verifier)
{
    internal BoundMaintenancePayload Read(Guid journalId, BlobReference? expectedPlan = null,
        MaintenanceRestorationInputs? contents = null)
    {
        if (database.InstanceId != instance.InstanceId ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot)) throw Invalid();
        var registered = new RecoveryJournalRegistry(database).ReadJournal(journalId);
        if (registered.Kind != JournalKind.UpdateApply || registered.Phase is not
            (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Reverting or
             JournalPhase.Reverted or JournalPhase.Committed)) throw Invalid();
        if (expectedPlan is not null && (registered.PlanPath != expectedPlan.RelativePath ||
            registered.PlanDigest != expectedPlan.Sha256)) throw Invalid();
        var reference = expectedPlan ?? new(registered.PlanPath,
            new FileInfo(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, registered.PlanPath))).Length,
            registered.PlanDigest);
        contents?.Require(instance, reference, verifier);
        var codec = new PayloadPlanCodec(instance, contracts, new(instance, contracts), roots);
        var plan = contents is null ? codec.Read(reference) : codec.ReadStructure(reference);
        if (plan.OperationId != registered.OperationId || plan.JournalId != registered.JournalId ||
            plan.Kind != registered.Kind || !plan.ResourceKeys.SequenceEqual(registered.ResourceKeys) ||
            plan.MaintenanceSource is null || plan.ExtensionSource is not null) throw Invalid();
        var sourceCodec = new UpdateSourceCodec(instance, contracts);
        var source = sourceCodec.ReadMetadata(plan.MaintenanceSource);
        sourceCodec.RequireFrozenBinding(source, plan);
        _ = contents is null
            ? sourceCodec.RequireTrustedContents(source, plan, database, verifier, CancellationToken.None)
            : sourceCodec.RequireTrustedFeed(source, plan, database, verifier, CancellationToken.None);
        var history = new JournalStore(instance, contracts).ReadAllRevisions(journalId).ToImmutableArray();
        var projected = history.SingleOrDefault(item => item.Revision == registered.Revision) ?? throw Invalid();
        if (projected.Phase != registered.Phase) throw Invalid();
        foreach (var document in history)
        {
            if (document.Kind != plan.Kind || document.InstanceId != instance.InstanceId ||
                document.Root.GetProperty("operationId").GetGuid() != plan.OperationId ||
                document.PlanPath != reference.RelativePath || document.PlanDigest != reference.Sha256 ||
                !document.Root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()!).SequenceEqual(plan.ResourceKeys) ||
                document.Root.GetProperty("participants").EnumerateArray().Any(item => !MaintenancePhysicalCodec.IsParticipant(item) &&
                    !MaintenanceRestorationCodec.IsParticipant(item) && !MaintenanceCommitCodec.IsParticipant(item))) throw Invalid();
        }
        var firstRestoration = history.FirstOrDefault(document => document.Root.GetProperty("participants").EnumerateArray()
            .Any(MaintenanceRestorationCodec.IsParticipant));
        var physicalHistory = history.Where(document => firstRestoration is null || document.Revision < firstRestoration.Revision).ToArray();
        var physical = new MaintenancePhysicalCodec(instance, contracts).ReadHistory(physicalHistory, plan, reference.Sha256);
        var restoration = new MaintenanceRestorationCodec(instance, contracts).ReadHistory(history, plan, reference, physical);
        var commit = new MaintenanceCommitCodec(instance, contracts).ReadHistory(history, plan, reference, source, physical);
        if (commit is not null && !restoration.IsEmpty) throw Invalid();
        foreach (var document in history)
        {
            if (document.Root.GetProperty("participants").EnumerateArray().Any(MaintenanceRestorationCodec.IsParticipant))
                MaintenanceRestorationCodec.RequireRevision(document, plan, restoration, physical.Length);
            else if (document.Root.GetProperty("participants").EnumerateArray().Any(MaintenanceCommitCodec.IsParticipant))
            {
                if (commit is null || document.Root.GetProperty("participants").GetArrayLength() != physical.Length + 1) throw Invalid();
            }
            else if (document.Root.GetProperty("receipts").GetArrayLength() != 0 ||
                document.Root.GetProperty("participants").GetArrayLength() != MaintenancePhysicalCodec.ReadReferences(document.Root, plan).Length)
                throw Invalid();
        }
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var operation = RequireOperationInside(connection, transaction, registered, source);
        var result = new BoundMaintenancePayload(plan, reference, source, registered, operation, history, physical, restoration, commit);
        RequireInside(connection, transaction, result);
        transaction.Commit();
        return result;
    }

    internal void RequireInside(SqliteConnection connection, SqliteTransaction transaction, BoundMaintenancePayload bound)
    {
        var current = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, bound.Plan.JournalId);
        if (current.OperationId != bound.Registered.OperationId || current.Kind != bound.Registered.Kind ||
            current.Phase != bound.Registered.Phase || current.Revision != bound.Registered.Revision ||
            current.PlanPath != bound.PlanReference.RelativePath || current.PlanDigest != bound.PlanReference.Sha256 ||
            !current.ResourceKeys.SequenceEqual(bound.Registered.ResourceKeys) ||
            RequireOperationInside(connection, transaction, current, bound.Source) != bound.Operation) throw Invalid();
        UpdateFeedRows.RequireAccepted(connection, transaction, bound.Source.Feed);
        if (!bound.Restoration.IsEmpty && bound.Restoration[0].Record.Failure is { } failure)
        {
            if (failure.HandoffId != bound.Operation.HandoffId) throw Invalid();
            MaintenanceRestorationCodec.RequireFailureInside(connection, transaction, bound.Plan.JournalId, failure);
        }
        JournalEffectGate.Require(connection, transaction, bound.Plan.JournalId, EffectGateState.Entered);
        if (bound.Commit is { } commit && (commit.HandoffId != bound.Operation.HandoffId ||
            commit.Intent.BeforeRevision + 1 != bound.Latest.Revision || bound.Latest.Phase != JournalPhase.Committed)) throw Invalid();
        MaintenanceEffectCursor.RequireEffectsInside(connection, transaction, bound.Registered,
            bound.Plan, bound.History, bound.Physical, bound.Restoration);
        if (bound.Registered.Phase == JournalPhase.Committed && (bound.Latest.Revision != bound.Registered.Revision ||
            (bound.Commit is not null ? bound.Operation.TerminalCode != "candidate_started" :
                bound.Restoration.IsEmpty || bound.Restoration[^1].Record.Stage != "committed" ||
                bound.Operation.TerminalCode != "maintenance_original_restored"))) throw Invalid();
    }

    private static MaintenanceOperationRow RequireOperationInside(SqliteConnection connection,
        SqliteTransaction transaction, RegisteredJournal registered, HostUpdateSource source)
    {
        var operation = MaintenanceOperationRows.ReadAll(connection, transaction)
            .SingleOrDefault(item => item.Identity.OperationId == registered.OperationId) ?? throw Invalid();
        if (operation.State != (registered.Phase == JournalPhase.Committed ? HostUpdateState.Completed : HostUpdateState.Starting) ||
            registered.Kind != JournalKind.UpdateApply || !registered.ResourceKeys.SequenceEqual(["maintenance:host-payload"]) ||
            operation.HandoffId is null ||
            operation.InitialJournalId != registered.JournalId || operation.Plan.RelativePath != registered.PlanPath ||
            operation.Plan.Sha256 != registered.PlanDigest ||
            UpdateRequestIdentity.FromSource(source, operation.Identity.SubjectId) != operation.Identity) throw Invalid();
        return operation;
    }

    private static IOException Invalid() => new("maintenance_payload_binding_unverified");
}
