using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionPayloadRestoration(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots, InstanceLease lease, JournalRecoveryOwnership ownership,
    IPayloadPersistenceObserver? observer = null)
{
    private readonly ExtensionPayloadBinding _binding = new(instance, database, contracts, roots);
    private readonly JournalStore _journals = new(instance, contracts);
    private readonly ExtensionRestorationCodec _codec = new(instance, contracts);
    private readonly ExtensionTreeEffects _trees = new(new(new(4096, 268435456)),
        new(new(new(4096, 268435456)), new(instance, contracts)));

    internal JournalRecoveryResult Recover(Guid journalId)
    {
        lease.VerifyHeld(instance);
        var stop = ownership.CaptureExtensionWriters(instance, database, lease, journalId);
        return Run(journalId, stop);
    }

    internal JournalRecoveryResult RecoverFailedCallback(PreparedExtensionActivation.FailedCallback failure)
    {
        lease.VerifyHeld(instance);
        failure.Require(instance, database, lease, failure.JournalId);
        var stop = ownership.CaptureFailedActivation(instance, database, lease, failure);
        return Run(failure.JournalId, stop, failure.Plan);
    }

    private JournalRecoveryResult Run(Guid journalId, JournalRecoveryOwnership.ExtensionStopProof stop,
        BlobReference? expectedPlan = null)
    {
        var original = _binding.Read(journalId, expectedPlan); RequireRecoverable(original, stop.Failure);
        using var contents = ExtensionRestorationInputs.Hold(instance, contracts, original);
        for (var count = 0; count < 8; count++)
        {
            var bound = lease.PublishExecution(() => Synchronize(journalId, original.PlanReference, stop));
            RequireRecoverable(bound, stop.Failure);
            if (bound.Registered.Phase == JournalPhase.Committed) return JournalRecoveryResult.Completed;
            lease.RequireReplaceableNative(bound.Source.ExtensionId, bound.Plan.TargetPath);
            ExtensionRestorationRecord first;
            if (bound.Restoration.IsEmpty)
            {
                var scene = _trees.ObserveRestoration(bound.Plan, bound.Physical[^1].Record);
                first = new(ExtensionRestorationCodec.Format, 1, instance.InstanceId,
                    bound.Plan.OperationId, journalId, bound.PlanReference, bound.Plan.ExtensionSource!,
                    bound.Physical[^1].Intent.Artifact, bound.Latest.Revision, "started",
                    scene.RetireCandidate, scene.RestoreOriginal, stop.Failure);
            }
            else first = bound.Restoration[0].Record;
            var stage = ExtensionRestorationCodec.Sequence(first)[bound.Restoration.Length];
            lease.PublishExecution(() => RequireAdmission(bound, stop));
            switch (stage)
            {
                case "started": break;
                case "candidate-retired": _trees.RetireCandidate(bound.Plan, bound.Physical[^1].Record); break;
                case "original-restored": _trees.RestoreOriginal(bound.Plan, bound.Physical[^1].Record); break;
                case "restored" or "committed":
                    using (var restored = _trees.HoldRestored(bound.Plan, bound.Physical[^1].Record))
                        lease.PublishExecution(() => Append(bound, first with
                        { Stage = stage, BeforeRevision = bound.Latest.Revision }, stop));
                    continue;
                default: throw Invalid();
            }
            lease.PublishExecution(() => Append(bound, first with
            { Stage = stage, BeforeRevision = bound.Latest.Revision }, stop));
        }
        throw Invalid();
    }

    private BoundExtensionPayload Synchronize(Guid journalId, BlobReference originalPlan,
        JournalRecoveryOwnership.ExtensionStopProof stop)
    {
        var bound = _binding.Read(journalId, originalPlan); RequireRecoverable(bound, stop.Failure);
        foreach (var document in bound.History.Where(item => item.Revision > bound.Registered.Revision))
        {
            var current = bound;
            var restoration = bound.Restoration.SingleOrDefault(item => item.Intent.BeforeRevision == document.Revision - 1);
            var physical = bound.Physical.SingleOrDefault(item => item.Intent.BeforeRevision == document.Revision - 1);
            if ((restoration is null) == (physical is null)) throw Invalid();
            using var restored = restoration?.Record.Stage is "restored" or "committed"
                ? _trees.HoldRestored(bound.Plan, bound.Physical[^1].Record) : null;
            database.Write((connection, transaction) =>
            {
                if (restoration is not null)
                    ExtensionRestorationProjection.Advance(database, connection, transaction, current, restoration, document, stop, restored);
                else ExtensionRestorationProjection.SynchronizePhysical(database, connection, transaction, current, physical!, document, stop);
                return true;
            });
            observer?.Reached(PayloadPersistencePoint.SqlProjectionCommitted, journalId, document.Revision, document.Phase);
            bound = _binding.Read(journalId, originalPlan);
        }
        return bound;
    }

    private void RequireAdmission(BoundExtensionPayload expected, JournalRecoveryOwnership.ExtensionStopProof stop)
    {
        lease.VerifyHeld(instance);
        var bound = _binding.Read(expected.Plan.JournalId, expected.PlanReference); RequireRecoverable(bound, stop.Failure);
        if (bound.Registered.Revision != expected.Registered.Revision || bound.Latest.Revision != expected.Latest.Revision ||
            !bound.Latest.Export().AsSpan().SequenceEqual(expected.Latest.Export())) throw Invalid();
        database.Write((connection, transaction) =>
        {
            stop.RequireInside(connection, transaction, bound.Plan.JournalId);
            var actual = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, bound.Plan.JournalId);
            if (actual.Revision != bound.Registered.Revision || actual.Phase != bound.Registered.Phase ||
                actual.PlanPath != bound.PlanReference.RelativePath || actual.PlanDigest != bound.PlanReference.Sha256 ||
                !actual.ResourceKeys.SequenceEqual(bound.Plan.ResourceKeys)) throw Invalid();
            ExtensionInstalledRows.RequireExpectedInside(connection, transaction, bound.Source.ExtensionId,
                bound.Source.ExpectedInstalledRevision, bound.Source.OriginalFootprint);
            JournalEffectGate.Require(connection, transaction, actual.JournalId, EffectGateState.Entered);
            return true;
        });
    }

    private void Append(BoundExtensionPayload bound, ExtensionRestorationRecord value,
        JournalRecoveryOwnership.ExtensionStopProof stop)
    {
        RequireAdmission(bound, stop);
        if (bound.Latest.Revision != bound.Registered.Revision) throw Invalid();
        if (value.Stage == "started")
        {
            var scene = _trees.ObserveRestoration(bound.Plan, bound.Physical[^1].Record);
            if (scene != (value.RetireCandidate, value.RestoreOriginal)) throw Invalid();
        }
        var artifact = _codec.Persist(value, bound.Plan, bound.PlanReference, bound.Physical[^1].Intent.Artifact);
        var document = JsonNode.Parse(bound.Latest.Export())!.AsObject();
        document["revision"] = bound.Latest.Revision + 1;
        document["phase"] = FileEffectRows.Wire(ExtensionRestorationCodec.Phase(value.Stage));
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        document["participants"]!.AsArray().Add(ExtensionRestorationCodec.Participant(
            new(ExtensionRestorationCodec.Format, 1, value.BeforeRevision, value.Stage, artifact), bound.Source.ExtensionId));
        if (value.Stage == "committed")
            document["receipts"]!.AsArray().Add(new JsonObject
            {
                ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = document["updatedAt"]!.GetValue<string>(),
                ["stepId"] = "extension-restoration", ["effect"] = "reverted", ["detailCode"] = "extension_original_restored"
            });
        var appended = _journals.Append(bound.Plan.JournalId, bound.Latest.Revision,
            BoundedJson.Serialize(document), hasVerifiedRecoveryEvidence: true);
        observer?.Reached(PayloadPersistencePoint.JournalFlushed, appended.JournalId, appended.Revision, appended.Phase);
        _ = Synchronize(bound.Plan.JournalId, bound.PlanReference, stop);
    }

    private static void RequireRecoverable(BoundExtensionPayload bound, ActivationFailureFacts? liveFailure = null)
    {
        var recordedFailure = bound.Restoration.IsEmpty ? null : bound.Restoration[0].Record.Failure;
        var failure = recordedFailure ?? liveFailure;
        var full = bound.Physical.Length == ExtensionPhysicalCodec.Sequence(bound.Plan).Length;
        if (bound.Plan.Kind != JournalKind.ExtensionApply || bound.Preparation is not null || bound.Commit is not null ||
            bound.Physical.IsEmpty || (full != (failure is not null)) ||
            recordedFailure is not null && liveFailure is not null && recordedFailure != liveFailure ||
            bound.Registered.Phase is not (JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Reverting or JournalPhase.Reverted or JournalPhase.Committed) ||
            bound.Latest.Phase is not (JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Reverting or JournalPhase.Reverted or JournalPhase.Committed) ||
            bound.Restoration.IsEmpty && bound.Latest.Phase != (full ? JournalPhase.Applied : JournalPhase.Applying) ||
            bound.Registered.Phase == JournalPhase.Committed &&
                (bound.Restoration.IsEmpty || bound.Restoration[^1].Record.Stage != "committed")) throw Invalid();
    }
    private static IOException Invalid() => new("extension_restoration_unverified");
}
