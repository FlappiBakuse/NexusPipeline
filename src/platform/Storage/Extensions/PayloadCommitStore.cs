using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class PayloadCommitStore(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots, IPayloadPersistenceObserver? observer = null)
{
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    private readonly JournalStore _journals = new(instance, contracts);
    private readonly RecoveryJournalRegistry _registry = new(database);
    private readonly ExtensionPayloadBinding _binding = new(instance, database, contracts, roots);

    internal BlobReference PrepareActivation(PreparedExtensionPayload prepared,
        ExtensionActivator.ApplyReservation inventory, ExtensionApplyActivation activation,
        CancellationToken acceptedCancellation) =>
        prepared.WithPhysical(inventory, acceptedCancellation, (plan, source, root, lease, _, identity, _, reference) =>
        {
            if (activation.Source.Root != root || activation.Source.Source != plan.ExtensionSource) throw Invalid();
            var result = lease.PublishExecution(() =>
            {
                var bound = _binding.Read(plan.JournalId, reference);
                RequireApplied(bound);
                activation.RequirePreparedInsidePublication();
                if (activation.Source.PhysicalResult != bound.Physical[^1].Intent.Artifact) throw Invalid();
                if (bound.Preparation is { } existing)
                {
                    if (existing.ActivationId != activation.ActivationId) throw Invalid();
                    SynchronizePreparation(bound);
                    return bound.PreparationIntent!.Artifact;
                }
                if (bound.Registered.Revision != bound.Latest.Revision) throw Invalid();
                RequireCurrent(bound);
                var value = new ExtensionActivationPreparation(ActivationPreparationCodec.Format, 1,
                    instance.InstanceId, plan.OperationId, plan.JournalId, bound.Latest.Revision,
                    plan.ExtensionSource!, bound.Physical[^1].Intent.Artifact, activation.ActivationId);
                var codec = new ActivationPreparationCodec(instance, contracts);
                codec.RequireBinding(value, plan, source, bound.Physical[^1]);
                var artifact = _blobs.PersistPlan(codec.EncodeMetadata(value));
                var intent = new ActivationPreparationIntent(ActivationPreparationCodec.Format,
                    1, bound.Latest.Revision, artifact);
                var document = Next(bound, "applied");
                document["participants"]!.AsArray().Add(ActivationPreparationCodec.Participant(intent, source.ExtensionId));
                var appended = _journals.Append(plan.JournalId, bound.Latest.Revision, BoundedJson.Serialize(document));
                observer?.Reached(PayloadPersistencePoint.JournalFlushed, plan.JournalId, appended.Revision, appended.Phase);
                SynchronizePreparation(_binding.Read(plan.JournalId, reference));
                return artifact;
            });
            observer?.Reached(PayloadPersistencePoint.SqlProjectionCommitted, plan.JournalId,
                _registry.ReadJournal(plan.JournalId).Revision, JournalPhase.Applied);
            return result;
        });

    internal BoundExtensionPayload Commit(PreparedExtensionPayload prepared,
        ExtensionActivator.ApplyReservation inventory, ExtensionApplyActivation? activation,
        CancellationToken acceptedCancellation)
    {
        if (prepared.ReadCommitted() is { } committed) return committed;
        return prepared.WithPhysical(inventory, acceptedCancellation, (plan, source, root, lease, policy, identity, _, reference) =>
        {
            if (policy.MustActivateCandidate != (activation is not null) ||
                activation is not null && (activation.Source.Root != root || activation.Source.Source != plan.ExtensionSource))
                throw Invalid();
            var result = lease.PublishExecution(() =>
            {
                var bound = _binding.Read(plan.JournalId, reference);
                if (bound.Registered.Phase == JournalPhase.Committed)
                {
                    RequireActivation(bound, activation);
                    return bound;
                }
                RequireApplied(bound);
                RequireActivation(bound, activation);
                var decision = ApplyCompletionPolicy.Decide(policy, new(
                    plan.Action == PayloadAction.Remove ? ExtensionPayloadObservation.OwnedTargetRemoved :
                        ExtensionPayloadObservation.CandidateInstalled,
                    bound.Physical[^1].Record.PreviousScopeClosure == "stopped" ? ExtensionScopeClosure.Stopped : ExtensionScopeClosure.NotRequired,
                    activation is null ? ExtensionActivationObservation.NotAttempted : ExtensionActivationObservation.Published));
                if (decision.Disposition != (plan.Action == PayloadAction.Remove
                        ? ExtensionApplyDisposition.CommitRemoved : ExtensionApplyDisposition.CommitInstalled)) throw Invalid();
                using var held = new ExtensionTreeEffects(new(new(4096, 268435456)),
                    new(new(new(4096, 268435456)), _blobs)).HoldInstalled(plan, bound.Physical[^1].Record);
                if (bound.Commit is null) bound = RecordCommit(bound, activation);
                SynchronizeCommit(bound, activation);
                return _binding.Read(plan.JournalId, reference);
            });
            observer?.Reached(PayloadPersistencePoint.SqlProjectionCommitted, plan.JournalId,
                result.Registered.Revision, JournalPhase.Committed);
            return result;
        });
    }

    internal JournalRecoveryResult RecoverRemoved(InstanceLease lease, JournalRecoveryOwnership ownership, Guid journalId)
    {
        lease.VerifyHeld(instance);
        var stop = ownership.CaptureExtensionWriters(instance, database, lease, journalId);
        lease.PublishExecution(() =>
        {
            var bound = _binding.Read(journalId);
            RequireApplied(bound); RequireActivation(bound, null);
            if (bound.Plan.Action != PayloadAction.Remove || bound.Latest.Phase != JournalPhase.Applied ||
                bound.Registered.Revision != bound.Latest.Revision || bound.Commit is not null ||
                !bound.Restoration.IsEmpty) throw Invalid();
            using var held = new ExtensionTreeEffects(new(new(4096, 268435456)),
                new(new(new(4096, 268435456)), _blobs)).HoldInstalled(bound.Plan, bound.Physical[^1].Record);
            // Removal has no activation callback whose unknown effects could be replayed.
            _ = RecordCommit(bound, null, () => RequireStoppedRemoval(bound, stop));
        });
        return new ExtensionCommittedReconciliation(instance, database, contracts, roots, lease, ownership).Reconcile(journalId);
    }

    private void RequireStoppedRemoval(BoundExtensionPayload bound, JournalRecoveryOwnership.ExtensionStopProof stop) =>
        database.Write((connection, transaction) =>
        {
            var journalId = bound.Plan.JournalId;
            stop.RequireInside(connection, transaction, journalId);
            var current = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, journalId);
            if (current.Kind != JournalKind.ExtensionApply || current.OperationId != bound.Source.OperationId ||
                current.Phase != JournalPhase.Applied || current.Revision != bound.Registered.Revision ||
                current.PlanPath != bound.PlanReference.RelativePath || current.PlanDigest != bound.PlanReference.Sha256 ||
                !current.ResourceKeys.SequenceEqual(bound.Plan.ResourceKeys) ||
                OperationRows.ReadExisting(connection, transaction, bound.Identity) is not { Status: "accepted" }) throw Invalid();
            ExtensionInstalledRows.RequireExpectedInside(connection, transaction, bound.Source.ExtensionId,
                bound.Source.ExpectedInstalledRevision, bound.Source.OriginalFootprint);
            HostSettingsStore.RequireExtensionIntentInside(connection, transaction, bound.Source.ExtensionId,
                new(bound.Source.SettingsRevision, bound.Source.DesiredEnabled, bound.Source.InitializeEnabledPreference));
            JournalEffectGate.Require(connection, transaction, journalId, EffectGateState.Entered);
            PayloadEffectCursor.RequireEffectsInside(connection, transaction, current, bound.Plan, bound.History, bound.Physical);
            return true;
        });

    private BoundExtensionPayload RecordCommit(BoundExtensionPayload bound, ExtensionApplyActivation? activation,
        Action? requireCurrent = null)
    {
        var plan = bound.Plan; var source = bound.Source;
        if (bound.Latest.Revision != bound.Registered.Revision) throw Invalid();
        if (requireCurrent is null) RequireCurrent(bound); else requireCurrent();
        var footprint = Footprint(bound);
        var footprintCodec = new InstalledFootprintCodec(instance, roots, contracts);
        var footprintReference = _blobs.PersistPlan(footprintCodec.EncodeMetadata(footprint));
        var value = new ExtensionInstallationCommit(InstallationCommitCodec.Format, 1,
            instance.InstanceId, plan.OperationId, plan.JournalId, bound.Latest.Revision,
            plan.ExtensionSource!, bound.Physical[^1].Intent.Artifact, footprintReference,
            bound.PreparationIntent?.Artifact, footprint.Revision, source.SettingsRevision,
            checked(source.SettingsRevision + (source.InitializeEnabledPreference ? 1 : 0)),
            source.DesiredEnabled, source.InitializeEnabledPreference,
            activation is null ? "not-attempted" : "published", activation?.ActivationId);
        var codec = new InstallationCommitCodec(instance, contracts);
        footprintCodec.RequireBinding(footprint, ExtensionPayloadBinding.DerivedInstallation(plan, value, footprint),
            plan, plan.ExtensionSource!, bound.Physical[^1]);
        codec.RequireBinding(value, plan, source, bound.Physical[^1], footprint, footprintReference,
            bound.Preparation, bound.PreparationIntent?.Artifact);
        var artifact = _blobs.PersistPlan(codec.EncodeMetadata(value));
        var document = Next(bound, "committed");
        document["participants"]!.AsArray().Add(InstallationCommitCodec.Participant(
            new(InstallationCommitCodec.Format, 1, value.BeforeRevision, artifact), source.ExtensionId));
        document["receipts"]!.AsArray().Add(new JsonObject
        {
            ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = document["updatedAt"]!.GetValue<string>(),
            ["stepId"] = "extension-installation", ["effect"] = "completed",
            ["detailCode"] = plan.Action == PayloadAction.Remove ? "extension_removed" : "extension_installed"
        });
        var appended = _journals.Append(plan.JournalId, bound.Latest.Revision, BoundedJson.Serialize(document));
        observer?.Reached(PayloadPersistencePoint.JournalFlushed, plan.JournalId, appended.Revision, appended.Phase);
        return _binding.Read(plan.JournalId, bound.PlanReference);
    }

    private void SynchronizePreparation(BoundExtensionPayload bound)
    {
        RequireApplied(bound);
        if (bound.Preparation is null || bound.Latest.Phase != JournalPhase.Applied) throw Invalid();
        if (bound.Registered.Revision == bound.Latest.Revision) { RequireCurrent(bound); return; }
        if (bound.Latest.Revision != bound.Registered.Revision + 1 ||
            bound.Preparation.BeforeRevision != bound.Registered.Revision) throw Invalid();
        database.Write((connection, transaction) =>
        {
            PayloadEffectCursor.RequireInside(connection, transaction, bound.Registered, bound.Source, bound.Identity);
            JournalEffectGate.Require(connection, transaction, bound.Plan.JournalId, EffectGateState.Entered);
            _registry.AdvanceInside(connection, transaction, bound.Registered with { Revision = bound.Latest.Revision },
                bound.Registered.Revision);
            return true;
        });
    }

    private void SynchronizeCommit(BoundExtensionPayload bound, ExtensionApplyActivation? activation)
    {
        if (bound.Commit is null || bound.Footprint is null || bound.Latest.Phase != JournalPhase.Committed ||
            bound.Registered.Phase != JournalPhase.Applied ||
            bound.Latest.Revision != bound.Registered.Revision + 1) throw Invalid();
        database.Write((connection, transaction) =>
        {
            PayloadEffectCursor.RequireInside(connection, transaction, bound.Registered, bound.Source, bound.Identity);
            JournalEffectGate.Require(connection, transaction, bound.Plan.JournalId, EffectGateState.Entered);
            activation?.RequirePublishedInsidePublication();
            var settingsRevision = HostSettingsStore.CommitExtensionPreferenceInside(connection, transaction,
                bound.Plan.JournalId, bound.Source.ExtensionId,
                new(bound.Source.SettingsRevision, bound.Source.DesiredEnabled, bound.Source.InitializeEnabledPreference));
            if (settingsRevision != bound.Commit.SettingsAfterRevision) throw Invalid();
            ExtensionInstalledRows.InsertInside(connection, transaction, bound.Source, bound.Footprint, bound.Commit.Footprint,
                ExtensionUpgradeSource.AllowsSameVersion(instance, contracts, roots, bound.Source));
            _registry.AdvanceInside(connection, transaction, bound.Registered with
                { Revision = bound.Latest.Revision, Phase = JournalPhase.Committed }, bound.Registered.Revision);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation " +
                "AND status='accepted' AND initial_journal_id=$journal;";
            command.Parameters.AddWithValue("$operation", bound.Plan.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$journal", bound.Plan.JournalId.ToString("D"));
            if (command.ExecuteNonQuery() != 1) throw Invalid();
            return true;
        });
    }

    private void RequireCurrent(BoundExtensionPayload bound) =>
        database.Write((connection, transaction) =>
        {
            PayloadEffectCursor.RequireInside(connection, transaction, bound.Registered, bound.Source, bound.Identity);
            JournalEffectGate.Require(connection, transaction, bound.Plan.JournalId, EffectGateState.Entered);
            return true;
        });

    private ExtensionInstalledFootprint Footprint(BoundExtensionPayload bound)
    {
        var source = bound.Source;
        var package = source.Package is { } candidate
            ? new ExtensionPackageIdentity(source.ExtensionId, candidate.Entry.PackageVersion,
                candidate.Entry.Kind == "managed-code" ? ExtensionPackageKind.ManagedCode : ExtensionPackageKind.DataSpecialized,
                candidate.Entry.ArtifactSha256, candidate.Entry.ManifestSha256)
            : new InstalledFootprintCodec(instance, roots, contracts).ReadMetadata(source.OriginalFootprint ?? throw Invalid()).Package;
        var payload = bound.Plan.Action == PayloadAction.Remove
            ? new ArchivedFileTree(new(FileObjectKind.Missing, []), [], [])
            : bound.Plan.Candidate with { OriginalObjects = bound.Physical[^1].Record.CandidateObjects };
        return new(InstalledFootprintCodec.Format, 1, instance.InstanceId, source.OperationId, source.JournalId,
            source.ExtensionId, checked(source.ExpectedInstalledRevision + 1), bound.Plan.Action != PayloadAction.Remove,
            package, bound.Plan.TargetPath, bound.Plan.ParentIdentity, bound.Plan.ExtensionSource!,
            bound.Physical[^1].Intent.Artifact, payload);
    }

    private static void RequireApplied(BoundExtensionPayload bound)
    {
        if (bound.Registered.Phase != JournalPhase.Applied || bound.Physical.IsEmpty ||
            bound.Physical[^1].Record.Stage != ExtensionPhysicalCodec.Sequence(bound.Plan)[^1]) throw Invalid();
    }

    private static void RequireActivation(BoundExtensionPayload bound, ExtensionApplyActivation? activation)
    {
        if (activation is null)
        {
            if (bound.Preparation is not null || bound.Plan.Action != PayloadAction.Remove && bound.Source.DesiredEnabled)
                throw Invalid();
        }
        else
        {
            activation.RequirePublishedInsidePublication();
            if (bound.Preparation?.ActivationId != activation.ActivationId ||
                bound.PreparationIntent?.Artifact is null || bound.Commit is { } commit &&
                (commit.ActivationState != "published" || commit.ActivationId != activation.ActivationId)) throw Invalid();
        }
    }

    private static JsonObject Next(BoundExtensionPayload bound, string phase)
    {
        var document = JsonNode.Parse(bound.Latest.Export())!.AsObject();
        document["revision"] = bound.Latest.Revision + 1; document["phase"] = phase;
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        return document;
    }
    private static IOException Invalid() => new("extension_installation_commit_pending");
}
