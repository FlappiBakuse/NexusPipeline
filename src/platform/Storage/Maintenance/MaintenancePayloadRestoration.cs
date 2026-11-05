using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class MaintenancePayloadRestoration(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots, InstanceLease lease, JournalRecoveryOwnership ownership,
    UpdateFeedVerifier verifier, IPayloadPersistenceObserver? observer = null)
{
    private readonly MaintenancePayloadBinding _binding = new(instance, database, contracts, roots, verifier);
    private readonly JournalStore _journals = new(instance, contracts);
    private readonly MaintenanceRestorationCodec _codec = new(instance, contracts);
    private readonly ApplicationTreeEffects _trees = new(new(new(4096, 1073741824)),
        new(new(new(4096, 1073741824)), new(instance, contracts)));

    internal JournalRecoveryResult Recover(Guid journalId)
    {
        lease.VerifyHeld(instance);
        var stop = ownership.CaptureMaintenanceWriters(instance, database, lease, journalId);
        return Run(journalId, stop);
    }

    internal JournalRecoveryResult RecoverFailedApply(MaintenanceEffectSession.FailedEffect failure)
    {
        lease.VerifyHeld(instance);
        failure.Require(instance, database, lease, failure.JournalId);
        var stop = ownership.CaptureMaintenanceFailure(instance, database, lease, failure);
        return Run(failure.JournalId, stop, failure.Plan);
    }

    private JournalRecoveryResult Run(Guid journalId, JournalRecoveryOwnership.MaintenanceStopProof stop,
        BlobReference? expectedPlan = null)
    {
        using var paths = DirectoryAttributeScope.Hold(instance);
        var original = EnsureEnteredObservation(_binding.Read(journalId, expectedPlan), stop);
        RequireRecoverable(original, stop.Failure);
        new MaintenanceInstallation(instance, contracts).RequireOriginal(original.Source, original.Plan, verifier.PayloadProfile);
        using var contents = MaintenanceRestorationInputs.Hold(instance, database, contracts, verifier, original);
        for (var count = 0; count < 8; count++)
        {
            var bound = lease.PublishExecution(() => Synchronize(journalId, original.PlanReference, stop, contents));
            RequireRecoverable(bound, stop.Failure);
            if (bound.Registered.Phase == JournalPhase.Committed) return JournalRecoveryResult.Completed;
            MaintenanceRestorationRecord first;
            if (bound.Restoration.IsEmpty)
            {
                var scene = _trees.ObserveRestoration(bound.Plan, MaintenancePhysicalJournal.Scene(bound.Physical[^1].Record));
                first = new(MaintenanceRestorationCodec.Format, 1, instance.InstanceId,
                    bound.Plan.OperationId, journalId, bound.PlanReference, bound.Plan.MaintenanceSource!,
                    bound.Physical[^1].Intent.Artifact, bound.Latest.Revision, "started",
                    scene.RetireCandidate, scene.RestoreOriginal, stop.Failure);
            }
            else first = bound.Restoration[0].Record;
            var stage = MaintenanceRestorationCodec.Sequence(first)[bound.Restoration.Length];
            lease.PublishExecution(() => RequireAdmission(bound, stop, contents));
            switch (stage)
            {
                case "started": break;
                case "candidate-retired": _trees.RetireCandidate(bound.Plan, MaintenancePhysicalJournal.Scene(bound.Physical[^1].Record)); break;
                case "original-restored": _trees.RestoreOriginal(bound.Plan, MaintenancePhysicalJournal.Scene(bound.Physical[^1].Record)); break;
                case "restored" or "committed":
                    using (var restored = _trees.HoldRestored(bound.Plan, MaintenancePhysicalJournal.Scene(bound.Physical[^1].Record)))
                        lease.PublishExecution(() =>
                        {
                            RequireAdmission(bound, stop, contents);
                            new MaintenanceInstallation(instance, contracts).CancelPreparation(bound.Source, bound.Plan, verifier.PayloadProfile);
                            Append(bound, first with { Stage = stage, BeforeRevision = bound.Latest.Revision }, stop, contents);
                        });
                    continue;
                default: throw Invalid();
            }
            lease.PublishExecution(() => Append(bound, first with
            { Stage = stage, BeforeRevision = bound.Latest.Revision }, stop, contents));
        }
        throw Invalid();
    }

    private BoundMaintenancePayload Synchronize(Guid journalId, BlobReference originalPlan,
        JournalRecoveryOwnership.MaintenanceStopProof stop, MaintenanceRestorationInputs contents)
    {
        var bound = _binding.Read(journalId, originalPlan, contents); RequireRecoverable(bound, stop.Failure);
        foreach (var document in bound.History.Where(item => item.Revision > bound.Registered.Revision))
        {
            var current = bound;
            var restoration = bound.Restoration.SingleOrDefault(item => item.Intent.BeforeRevision == document.Revision - 1);
            var physical = bound.Physical.SingleOrDefault(item => item.Intent.BeforeRevision == document.Revision - 1);
            if ((restoration is null) == (physical is null)) throw Invalid();
            using var restored = restoration?.Record.Stage is "restored" or "committed"
                ? _trees.HoldRestored(bound.Plan, MaintenancePhysicalJournal.Scene(bound.Physical[^1].Record)) : null;
            database.Write((connection, transaction) =>
            {
                if (restoration is not null)
                    MaintenanceRestorationProjection.Advance(database, connection, transaction, current, restoration, document, stop, restored);
                else MaintenanceRestorationProjection.SynchronizePhysical(database, connection, transaction, current, physical!, document, stop);
                return true;
            });
            observer?.Reached(PayloadPersistencePoint.SqlProjectionCommitted, journalId, document.Revision, document.Phase);
            bound = _binding.Read(journalId, originalPlan, contents);
        }
        return bound;
    }

    private BoundMaintenancePayload EnsureEnteredObservation(BoundMaintenancePayload bound,
        JournalRecoveryOwnership.MaintenanceStopProof stop)
    {
        if (!bound.Physical.IsEmpty) return bound;
        if (!bound.Restoration.IsEmpty || bound.Registered.Phase != JournalPhase.Prepared ||
            bound.Registered.Revision != 1 || bound.Latest.Phase != JournalPhase.Prepared || bound.Latest.Revision != 1)
            throw Invalid();
        using var before = TreeReadLease.AcquireApplication(bound.Plan.TargetPath, bound.Plan.ParentIdentity, bound.Plan.Before);
        var observed = _trees.ObserveRestoration(bound.Plan,
            new(bound.Plan.OperationId, bound.Plan.JournalId, "old-family-stopped", null, []));
        if (observed != (false, false)) throw Invalid();
        lease.PublishExecution(() => database.Write((connection, transaction) =>
        {
            stop.RequireInside(connection, transaction, bound.Plan.JournalId);
            _binding.RequireInside(connection, transaction, bound);
            return true;
        }));
        var value = new MaintenancePhysicalRecord(MaintenancePhysicalCodec.Format, 1, instance.InstanceId,
            bound.Plan.OperationId, bound.Plan.JournalId, bound.PlanReference.Sha256, bound.Plan.MaintenanceSource!,
            1, "old-family-stopped", null, []);
        var artifact = new MaintenancePhysicalCodec(instance, contracts).Persist(value, bound.Plan);
        var document = JsonNode.Parse(bound.Latest.Export())!.AsObject();
        document["revision"] = 2;
        document["phase"] = "applying";
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        document["participants"]!.AsArray().Add(MaintenancePhysicalCodec.Participant(
            new(MaintenancePhysicalCodec.Format, 1, 1, "old-family-stopped", artifact)));
        lease.PublishExecution(() => _journals.Append(bound.Plan.JournalId, 1, BoundedJson.Serialize(document)));
        observer?.Reached(PayloadPersistencePoint.JournalFlushed, bound.Plan.JournalId, 2, JournalPhase.Applying);
        return _binding.Read(bound.Plan.JournalId, bound.PlanReference);
    }

    private void RequireAdmission(BoundMaintenancePayload expected, JournalRecoveryOwnership.MaintenanceStopProof stop,
        MaintenanceRestorationInputs contents)
    {
        lease.VerifyHeld(instance);
        var bound = _binding.Read(expected.Plan.JournalId, expected.PlanReference, contents); RequireRecoverable(bound, stop.Failure);
        new MaintenanceInstallation(instance, contracts).RequireOriginal(bound.Source, bound.Plan, verifier.PayloadProfile);
        if (bound.Registered.Revision != expected.Registered.Revision || bound.Latest.Revision != expected.Latest.Revision ||
            !bound.Latest.Export().AsSpan().SequenceEqual(expected.Latest.Export())) throw Invalid();
        database.Write((connection, transaction) =>
        {
            stop.RequireInside(connection, transaction, bound.Plan.JournalId);
            var actual = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, bound.Plan.JournalId);
            if (actual.Revision != bound.Registered.Revision || actual.Phase != bound.Registered.Phase ||
                actual.PlanPath != bound.PlanReference.RelativePath || actual.PlanDigest != bound.PlanReference.Sha256 ||
                !actual.ResourceKeys.SequenceEqual(bound.Plan.ResourceKeys)) throw Invalid();
            _binding.RequireInside(connection, transaction, bound);
            JournalEffectGate.Require(connection, transaction, actual.JournalId, EffectGateState.Entered);
            return true;
        });
    }

    private void Append(BoundMaintenancePayload bound, MaintenanceRestorationRecord value,
        JournalRecoveryOwnership.MaintenanceStopProof stop, MaintenanceRestorationInputs contents)
    {
        RequireAdmission(bound, stop, contents);
        if (bound.Latest.Revision != bound.Registered.Revision) throw Invalid();
        if (value.Stage == "started")
        {
            var scene = _trees.ObserveRestoration(bound.Plan, MaintenancePhysicalJournal.Scene(bound.Physical[^1].Record));
            if (scene != (value.RetireCandidate, value.RestoreOriginal)) throw Invalid();
        }
        var artifact = _codec.Persist(value, bound.Plan, bound.PlanReference, bound.Physical[^1].Intent.Artifact);
        var document = JsonNode.Parse(bound.Latest.Export())!.AsObject();
        document["revision"] = bound.Latest.Revision + 1;
        document["phase"] = FileEffectRows.Wire(MaintenanceRestorationCodec.Phase(value.Stage));
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        document["participants"]!.AsArray().Add(MaintenanceRestorationCodec.Participant(
            new(MaintenanceRestorationCodec.Format, 1, value.BeforeRevision, value.Stage, artifact)));
        if (value.Stage == "committed")
            document["receipts"]!.AsArray().Add(new JsonObject
            {
                ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = document["updatedAt"]!.GetValue<string>(),
                ["stepId"] = "maintenance-restoration", ["effect"] = "reverted", ["detailCode"] = "maintenance_original_restored"
            });
        var appended = _journals.Append(bound.Plan.JournalId, bound.Latest.Revision,
            BoundedJson.Serialize(document), hasVerifiedRecoveryEvidence: true);
        observer?.Reached(PayloadPersistencePoint.JournalFlushed, appended.JournalId, appended.Revision, appended.Phase);
        _ = Synchronize(bound.Plan.JournalId, bound.PlanReference, stop, contents);
    }

    private static void RequireRecoverable(BoundMaintenancePayload bound, HelperFailureFacts? liveFailure = null)
    {
        var recordedFailure = bound.Restoration.IsEmpty ? null : bound.Restoration[0].Record.Failure;
        var full = bound.Physical.Length == MaintenancePhysicalCodec.Sequence(bound.Plan).Length;
        if (bound.Plan.Kind != JournalKind.UpdateApply || bound.Physical.IsEmpty || bound.Commit is not null ||
            recordedFailure is not null && liveFailure is not null && recordedFailure != liveFailure ||
            bound.Registered.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Reverting or JournalPhase.Reverted or JournalPhase.Committed) ||
            bound.Latest.Phase is not (JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Reverting or JournalPhase.Reverted or JournalPhase.Committed) ||
            bound.Restoration.IsEmpty && bound.Latest.Phase != (full ? JournalPhase.Applied : JournalPhase.Applying) ||
            bound.Registered.Phase == JournalPhase.Committed &&
                (bound.Restoration.IsEmpty || bound.Restoration[^1].Record.Stage != "committed")) throw Invalid();
    }
    private static IOException Invalid() => new("maintenance_restoration_unverified");
}
