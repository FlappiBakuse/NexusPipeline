using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class MaintenancePhysicalJournal(MaintenanceHandoffInputs inputs,
    MaintenanceRegisteredActor actor, InstanceLease lease, Action requireOtherWritersStopped,
    IPayloadPersistenceObserver? observer)
{
    private readonly JournalStore _store = new(inputs.Instance, inputs.Contracts);
    private readonly RecoveryJournalRegistry _registry = new(inputs.Database);
    private readonly MaintenancePhysicalCodec _codec = new(inputs.Instance, inputs.Contracts);
    private readonly ApplicationTreeEffects _trees = new(new(new(4096, 1073741824)),
        new(new(new(4096, 1073741824)), new(inputs.Instance, inputs.Contracts)));

    internal MaintenancePhysicalRecord Apply(CancellationToken cancellation)
    {
        var plan = inputs.Plan;
        var sequence = MaintenancePhysicalCodec.Sequence(plan);
        for (var count = 0; count <= sequence.Length; count++)
        {
            requireOtherWritersStopped(); cancellation.ThrowIfCancellationRequested();
            var state = lease.PublishExecution(ReadAndSynchronize);
            if (state.Document.Phase == JournalPhase.Applied) return state.Entries[^1].Record;
            if (state.Entries.Length >= sequence.Length) throw Invalid();
            var next = sequence[state.Entries.Length];
            lease.PublishExecution(() => RequireAdmission(state));
            var previous = state.Entries.IsEmpty ? null : state.Entries[^1].Record;
            var observation = previous ?? new(MaintenancePhysicalCodec.Format, 1, inputs.Instance.InstanceId,
                plan.OperationId, plan.JournalId, state.Registered.PlanDigest, plan.MaintenanceSource!,
                state.Document.Revision, next, null, []);
            switch (next)
            {
                case "old-family-stopped": lease.PublishExecution(() => Enter(state)); break;
                case "workspace-created": observation = observation with { WorkspaceIdentity = _trees.CreateWorkspace(plan) }; break;
                case "candidate-materialized":
                    observation = observation with { CandidateObjects = _trees.MaterializeCandidate(plan, Scene(previous!), cancellation) }; break;
                case "original-preservation-started": break;
                case "original-preserved": _trees.PreserveOriginal(plan, Scene(previous!)); break;
                case "candidate-installed": _trees.InstallCandidate(plan, Scene(previous!)); break;
                default: throw Invalid();
            }
            if (next is "original-preserved" or "candidate-installed")
                observer?.Reached(PayloadPersistencePoint.PhysicalTreeMoved, plan.JournalId,
                    state.Document.Revision + 1, state.Document.Phase);
            observation = observation with { Stage = next, BeforeRevision = state.Document.Revision };
            lease.PublishExecution(() => Append(state, observation));
            observer?.Reached(PayloadPersistencePoint.SqlProjectionCommitted, plan.JournalId,
                state.Document.Revision + 1, next == "candidate-installed" ? JournalPhase.Applied : JournalPhase.Applying);
        }
        throw Invalid();
    }

    private void Enter(State state)
    {
        requireOtherWritersStopped();
        if (state.Registered.Phase != JournalPhase.Prepared || state.Document.Phase != JournalPhase.Prepared ||
            !state.Entries.IsEmpty) throw Invalid();
        var gate = new JournalEffectGate(inputs.Database);
        var current = gate.Read(state.Registered.JournalId);
        if (current.State != EffectGateState.Unentered) throw Invalid();
        gate.EnterMaintenance(state.Registered, current.Revision, (connection, transaction) =>
            MaintenanceEffectCursor.RequireInside(connection, transaction, state.Registered, inputs, actor));
    }

    private void RequireAdmission(State state)
    {
        requireOtherWritersStopped();
        inputs.Database.Write((connection, transaction) =>
        {
            MaintenanceEffectCursor.RequireInside(connection, transaction, state.Registered, inputs, actor);
            if (state.Document.Phase == JournalPhase.Applying)
                JournalEffectGate.Require(connection, transaction, state.Registered.JournalId, EffectGateState.Entered, true);
            else if (state.Document.Phase != JournalPhase.Prepared) throw Invalid();
            return true;
        });
    }

    private void Append(State state, MaintenancePhysicalRecord value)
    {
        RequireAdmission(state);
        var artifact = _codec.Persist(value, inputs.Plan);
        var intent = new MaintenancePhysicalIntent(MaintenancePhysicalCodec.Format, 1,
            value.BeforeRevision, value.Stage, artifact);
        var document = JsonNode.Parse(state.Document.Export())!.AsObject();
        document["revision"] = state.Document.Revision + 1;
        document["phase"] = value.Stage == "candidate-installed" ? "applied" : "applying";
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        document["participants"]!.AsArray().Add(MaintenancePhysicalCodec.Participant(intent));
        var appended = _store.Append(inputs.Plan.JournalId, state.Document.Revision, BoundedJson.Serialize(document));
        observer?.Reached(PayloadPersistencePoint.JournalFlushed, inputs.Plan.JournalId, appended.Revision, appended.Phase);
        _ = ReadAndSynchronize();
    }

    private State ReadAndSynchronize()
    {
        inputs.RequireCurrent(); actor.RequireCurrent(lease);
        var plan = inputs.Plan;
        var registered = _registry.ReadJournal(plan.JournalId);
        if (registered.Kind != JournalKind.UpdateApply || registered.OperationId != plan.OperationId ||
            registered.PlanPath != inputs.Operation.Plan.RelativePath || registered.PlanDigest != inputs.Operation.Plan.Sha256 ||
            !registered.ResourceKeys.SequenceEqual(plan.ResourceKeys)) throw Invalid();
        var history = _store.ReadAllRevisions(plan.JournalId);
        foreach (var document in history)
            if (document.Kind != registered.Kind || document.InstanceId != inputs.Instance.InstanceId ||
                document.PlanPath != registered.PlanPath || document.PlanDigest != registered.PlanDigest ||
                document.Root.GetProperty("operationId").GetGuid() != plan.OperationId ||
                !document.Root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()!).SequenceEqual(plan.ResourceKeys) ||
                document.Root.GetProperty("participants").EnumerateArray().Any(item => !MaintenancePhysicalCodec.IsParticipant(item)) ||
                document.Root.GetProperty("receipts").GetArrayLength() != 0 ||
                document.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied)) throw Invalid();
        var prefix = history.SingleOrDefault(item => item.Revision == registered.Revision) ?? throw Invalid();
        if (prefix.Phase != registered.Phase) throw Invalid();
        var entries = _codec.ReadHistory(history, plan, registered.PlanDigest);
        foreach (var document in history.Where(item => item.Revision > registered.Revision))
        {
            var entry = entries.Single(item => item.Intent.BeforeRevision == document.Revision - 1);
            var expected = registered;
            inputs.Database.Write((connection, transaction) =>
            {
                MaintenanceEffectCursor.RequireInside(connection, transaction, expected, inputs, actor);
                JournalEffectGate.Require(connection, transaction, plan.JournalId, EffectGateState.Entered);
                _registry.AdvanceInside(connection, transaction,
                    expected with { Revision = document.Revision, Phase = document.Phase }, expected.Revision);
                MaintenanceEffectCursor.RecordEffectsInside(connection, transaction, plan, document, entry.Record.Stage);
                return true;
            });
            registered = registered with { Revision = document.Revision, Phase = document.Phase };
        }
        using var connection = DatabaseConnections.Open(inputs.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        MaintenanceEffectCursor.RequireEffectsInside(connection, transaction, registered, plan, history, entries);
        transaction.Commit();
        return new(registered, history[^1], entries);
    }

    internal static PayloadTreeScene Scene(MaintenancePhysicalRecord record) =>
        new(record.OperationId, record.JournalId, record.Stage, record.WorkspaceIdentity, record.CandidateObjects);
    private sealed record State(RegisteredJournal Registered, JournalDocument Document,
        ImmutableArray<MaintenancePhysicalEntry> Entries);
    private static IOException Invalid() => new("maintenance_payload_physical_pending");
}
