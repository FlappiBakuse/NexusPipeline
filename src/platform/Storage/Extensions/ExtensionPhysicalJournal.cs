using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionPhysicalJournal(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots, IPayloadPersistenceObserver? observer = null)
{
    private readonly JournalStore _store = new(instance, contracts);
    private readonly RecoveryJournalRegistry _registry = new(database);
    private readonly ExtensionPhysicalCodec _codec = new(instance, contracts);
    private readonly ExtensionTreeEffects _trees = new(new(new(4096, 268435456)),
        new(new(new(4096, 268435456)), new(instance, contracts)));

    internal ExtensionPhysicalRecord Apply(PreparedExtensionPayload prepared,
        ExtensionActivator.ApplyReservation inventory, CancellationToken acceptedCancellation)
    {
        return prepared.WithPhysical(inventory, acceptedCancellation, (plan, source, root, lease, policy, identity, activation, originalPlan) =>
        {
            var sequence = ExtensionPhysicalCodec.Sequence(plan);
            for (var count = 0; count <= sequence.Length; count++)
            {
                var state = lease.PublishExecution(() => ReadAndSynchronize(plan, source, identity, originalPlan));
                if (state.Document.Phase == JournalPhase.Applied) return state.Entries[^1].Record;
                inventory.RequireNativeUnmapped();
                acceptedCancellation.ThrowIfCancellationRequested();
                if (!root.IsActive) throw Invalid();
                if (state.Entries.Length == sequence.Length) throw Invalid();
                var next = sequence[state.Entries.Length];
                lease.PublishExecution(() => RequireAdmission(state, source, identity));
                var previous = state.Entries.IsEmpty ? null : state.Entries[^1].Record;
                var observation = previous ?? new ExtensionPhysicalRecord(ExtensionPhysicalCodec.Format, 1,
                    instance.InstanceId, plan.OperationId, plan.JournalId, state.Registered.PlanDigest, plan.ExtensionSource!,
                    state.Document.Revision, next, activation, policy.MustDeactivateExistingScope ? "stopped" : "not-required", null, []);
                switch (next)
                {
                    case "scope-stopped":
                        lease.PublishExecution(() => Enter(state, source, identity));
                        break;
                    case "workspace-created":
                        observation = observation with { WorkspaceIdentity = _trees.CreateWorkspace(plan) };
                        break;
                    case "candidate-materialized":
                        observation = observation with { CandidateObjects = _trees.MaterializeCandidate(plan, previous!, acceptedCancellation) };
                        break;
                    case "original-preservation-started":
                        break;
                    case "original-preserved":
                        _trees.PreserveOriginal(plan, previous!); break;
                    case "candidate-installed":
                        _trees.InstallCandidate(plan, previous!); break;
                    case "target-removed":
                        _trees.RequireRemoved(plan, previous!); break;
                    default: throw Invalid();
                }
                if (next is "original-preserved" or "candidate-installed")
                    observer?.Reached(PayloadPersistencePoint.PhysicalTreeMoved, plan.JournalId,
                        state.Document.Revision + 1, state.Document.Phase);
                observation = observation with { Stage = next, BeforeRevision = state.Document.Revision };
                lease.PublishExecution(() => Append(state, plan, source, identity, observation));
                observer?.Reached(PayloadPersistencePoint.SqlProjectionCommitted, plan.JournalId,
                    state.Document.Revision + 1, next is "candidate-installed" or "target-removed" ? JournalPhase.Applied : JournalPhase.Applying);
            }
            throw Invalid();
        });
    }

    private void Enter(State state, ExtensionPayloadSource source, OperationIdentity identity)
    {
        if (state.Registered.Phase != JournalPhase.Prepared || state.Document.Phase != JournalPhase.Prepared ||
            !state.Entries.IsEmpty) throw Invalid();
        var gate = new JournalEffectGate(database);
        var current = gate.Read(state.Registered.JournalId);
        if (current.State == EffectGateState.Entered) return;
        if (current.State != EffectGateState.Unentered) throw Invalid();
        gate.EnterExtension(state.Registered, current.Revision, (connection, transaction) =>
            PayloadEffectCursor.RequireInside(connection, transaction, state.Registered, source, identity));
    }

    private void RequireAdmission(State state, ExtensionPayloadSource source, OperationIdentity identity)
    {
        RequireOriginalPlan(state.Registered, state.OriginalPlan);
        database.Write((connection, transaction) =>
        {
            PayloadEffectCursor.RequireInside(connection, transaction, state.Registered, source, identity);
            if (state.Document.Phase == JournalPhase.Applying)
                JournalEffectGate.Require(connection, transaction, state.Registered.JournalId, EffectGateState.Entered, true);
            else if (state.Document.Phase != JournalPhase.Prepared) throw Invalid();
            return true;
        });
    }

    private void Append(State state, FrozenPayloadApply plan, ExtensionPayloadSource source,
        OperationIdentity identity, ExtensionPhysicalRecord value)
    {
        RequireAdmission(state, source, identity);
        var artifact = _codec.Persist(value, plan);
        var intent = new ExtensionPhysicalIntent(ExtensionPhysicalCodec.Format, 1, value.BeforeRevision, value.Stage, artifact);
        var document = JsonNode.Parse(state.Document.Export())!.AsObject();
        document["revision"] = state.Document.Revision + 1;
        document["phase"] = value.Stage is "candidate-installed" or "target-removed" ? "applied" : "applying";
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        document["participants"]!.AsArray().Add(ExtensionPhysicalCodec.Participant(intent, source.ExtensionId));
        var appended = _store.Append(plan.JournalId, state.Document.Revision, BoundedJson.Serialize(document));
        observer?.Reached(PayloadPersistencePoint.JournalFlushed, plan.JournalId, appended.Revision, appended.Phase);
        _ = ReadAndSynchronize(plan, source, identity, state.OriginalPlan);
    }

    private State ReadAndSynchronize(FrozenPayloadApply plan, ExtensionPayloadSource source, OperationIdentity identity,
        BlobReference originalPlan)
    {
        var registered = _registry.ReadJournal(plan.JournalId);
        RequireOriginalPlan(registered, originalPlan);
        var sourceCodec = new PayloadSourceCodec(instance, contracts);
        var savedSource = sourceCodec.ReadMetadata(plan.ExtensionSource!);
        sourceCodec.RequireFrozenBinding(savedSource, plan);
        if (ExtensionApplyRequest.FromSource(savedSource).Identity(identity.OperationId, identity.SubjectId) != identity ||
            !savedSource.Claims.SequenceEqual(source.Claims)) throw Invalid();
        if (registered.Kind != JournalKind.ExtensionApply || registered.OperationId != plan.OperationId ||
            !registered.ResourceKeys.SequenceEqual(plan.ResourceKeys)) throw Invalid();
        var actual = new PayloadPlanCodec(instance, contracts, new(instance, contracts), roots)
            .ReadStructure(originalPlan);
        if (actual.OperationId != plan.OperationId || actual.JournalId != plan.JournalId || actual.ExtensionSource != plan.ExtensionSource)
            throw Invalid();
        var history = _store.ReadAllRevisions(plan.JournalId);
        foreach (var document in history)
            if (document.Kind != registered.Kind || document.InstanceId != instance.InstanceId ||
                document.PlanPath != registered.PlanPath || document.PlanDigest != registered.PlanDigest ||
                document.Root.GetProperty("operationId").GetGuid() != plan.OperationId ||
                !document.Root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()!).SequenceEqual(plan.ResourceKeys) ||
                document.Root.GetProperty("participants").EnumerateArray().Any(item => !ExtensionPhysicalCodec.IsParticipant(item)) ||
                document.Root.GetProperty("receipts").GetArrayLength() != 0 ||
                document.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied)) throw Invalid();
        var prefix = history.SingleOrDefault(item => item.Revision == registered.Revision) ?? throw Invalid();
        if (prefix.Phase != registered.Phase) throw Invalid();
        var entries = _codec.ReadHistory(history, plan, registered.PlanDigest);
        foreach (var document in history.Where(item => item.Revision > registered.Revision))
        {
            var entry = entries.Single(item => item.Intent.BeforeRevision == document.Revision - 1);
            var expected = registered;
            database.Write((connection, transaction) =>
            {
                PayloadEffectCursor.RequireInside(connection, transaction, expected, source, identity);
                _registry.AdvanceInside(connection, transaction,
                    expected with { Revision = document.Revision, Phase = document.Phase }, expected.Revision);
                RecordEffectsInside(connection, transaction, plan, document, entry.Record.Stage);
                return true;
            });
            registered = registered with { Revision = document.Revision, Phase = document.Phase };
        }
        RequireEffects(registered, plan, history, entries);
        return new(registered, history[^1], entries, originalPlan);
    }

    internal static void RecordEffectsInside(SqliteConnection connection, SqliteTransaction transaction,
        FrozenPayloadApply plan, JournalDocument document, string stage)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(document.Export()));
        void Move(string step, FileEffectState from, FileEffectState to) =>
            FileEffectOutbox.RecordInside(connection, transaction, plan.JournalId, step, from, to, digest);
        switch (stage)
        {
            case "scope-stopped":
                if (plan.Action == PayloadAction.Remove && plan.Before.Snapshot.Shape == FileObjectKind.Missing) break;
                Move(plan.Action == PayloadAction.Remove ? "preserve-original" : "install-candidate",
                    FileEffectState.Pending, FileEffectState.Started); break;
            case "original-preservation-started":
                Move("preserve-original", FileEffectState.Pending, FileEffectState.Started); break;
            case "original-preserved":
                Move("preserve-original", FileEffectState.Started, FileEffectState.Completed); break;
            case "candidate-installed":
                Move("install-candidate", FileEffectState.Started, FileEffectState.Completed); break;
        }
    }

    private void RequireEffects(RegisteredJournal registered, FrozenPayloadApply plan,
        IReadOnlyList<JournalDocument> history, ImmutableArray<ExtensionPhysicalEntry> entries)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
        using var transaction = connection.BeginTransaction(deferred: true);
        PayloadEffectCursor.RequireEffectsInside(connection, transaction, registered, plan, history, entries);
        transaction.Commit();
    }

    private sealed record State(RegisteredJournal Registered, JournalDocument Document,
        ImmutableArray<ExtensionPhysicalEntry> Entries, BlobReference OriginalPlan);
    private static void RequireOriginalPlan(RegisteredJournal registered, BlobReference originalPlan)
    {
        if (registered.PlanPath != originalPlan.RelativePath || registered.PlanDigest != originalPlan.Sha256)
            throw new IOException("extension_payload_plan_reference_changed");
    }

    private static IOException Invalid() => new("extension_payload_physical_pending");
}
