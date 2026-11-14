using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceCommitStore(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots, UpdateFeedVerifier verifier,
    IPayloadPersistenceObserver? observer = null)
{
    private readonly MaintenancePayloadBinding _binding = new(instance, database, contracts, roots, verifier);

    internal void Commit(InstanceLease lease, MaintenanceStartupClient startup, string startupJobName)
    {
        var completed = startup.RequireCompleted(lease);
        var challenge = completed.Challenge; var reply = completed.Reply;
        using var receipt = new StartupReceiptStore(instance, database, contracts, roots, verifier)
            .ReadPrepared(challenge, reply, ProcessIdentityReader.Read(Environment.ProcessId), startupJobName, CancellationToken.None);
        lease.PublishExecution(() =>
        {
            var bound = _binding.Read(challenge.Binding.JournalId, challenge.Binding.Plan);
            if (bound.Commit is not null || bound.Registered.Phase != JournalPhase.Applied || bound.Latest.Phase != JournalPhase.Applied ||
                bound.Registered.Revision != bound.Latest.Revision || !bound.Restoration.IsEmpty) throw Invalid();
            _ = startup.RequireCompleted(lease); receipt.RequireCurrent(database, challenge.Binding);
            var installation = new MaintenanceInstallation(instance, contracts)
                .PrepareCommit(bound.Source, bound.Plan, verifier.PayloadProfile);
            var document = JsonNode.Parse(bound.Latest.Export())!.AsObject();
            document["revision"] = bound.Latest.Revision + 1; document["phase"] = "committed";
            document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            document["participants"]!.AsArray().Add(MaintenanceCommitCodec.Participant(
                new(MaintenanceCommitCodec.Format, 1, bound.Latest.Revision, reply.Receipt) { Installation = installation }));
            document["receipts"]!.AsArray().Add(new JsonObject
            {
                ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = document["updatedAt"]!.GetValue<string>(),
                ["stepId"] = "maintenance-startup", ["effect"] = "completed", ["detailCode"] = "candidate_started"
            });
            var appended = new JournalStore(instance, contracts).Append(bound.Plan.JournalId, bound.Latest.Revision, BoundedJson.Serialize(document));
            observer?.Reached(PayloadPersistencePoint.JournalFlushed, appended.JournalId, appended.Revision, appended.Phase);
            Project(_binding.Read(bound.Plan.JournalId, bound.PlanReference), () => _ = startup.RequireCompleted(lease));
        });
    }

    internal JournalRecoveryResult ReconcileRecorded(InstanceLease lease, JournalRecoveryOwnership ownership, Guid journalId)
    {
        lease.VerifyHeld(instance);
        var recorded = _binding.Read(journalId);
        if (recorded.Registered.Phase == JournalPhase.Committed && recorded.Commit is not null)
            return JournalRecoveryResult.AlreadyCompleted;
        var stop = ownership.CaptureMaintenanceWriters(instance, database, lease, journalId);
        return lease.PublishExecution(() =>
        {
            var bound = _binding.Read(journalId);
            using var contents = MaintenanceRestorationInputs.Hold(instance, database, contracts, verifier, bound);
            Project(bound, () => lease.VerifyHeld(instance), (connection, transaction) => stop.RequireInside(connection, transaction, journalId));
            return JournalRecoveryResult.Completed;
        });
    }

    private void Project(BoundMaintenancePayload bound, Action requireCurrent,
        Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction>? requireRecovery = null)
    {
        if (bound.Commit is not { } commit || bound.Registered.Phase != JournalPhase.Applied ||
            bound.Latest.Phase != JournalPhase.Committed || bound.Latest.Revision != bound.Registered.Revision + 1 ||
            !bound.Restoration.IsEmpty || bound.Physical.IsEmpty) throw Invalid();
        var tree = ApplicationTree.Installed(bound.Plan, bound.Physical[^1].Record.CandidateObjects);
        using var held = TreeReadLease.AcquireApplication(bound.Plan.TargetPath, bound.Plan.ParentIdentity, tree);
        using var receipt = new ImmutableBlobStore(instance, contracts).OpenVerified(commit.Intent.Receipt);
        requireCurrent();
        new MaintenanceInstallation(instance, contracts).Publish(bound.Source, bound.Plan, verifier.PayloadProfile, commit);
        requireCurrent();
        database.Write((connection, transaction) =>
        {
            requireRecovery?.Invoke(connection, transaction);
            _binding.RequireInside(connection, transaction, bound);
            if (PhysicalObjectIdentity.Read(bound.Plan.TargetPath) != commit.InstalledRoot) throw Invalid();
            new RecoveryJournalRegistry(database).AdvanceInside(connection, transaction,
                bound.Registered with { Phase = JournalPhase.Committed, Revision = bound.Latest.Revision }, bound.Registered.Revision);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE system.maintenance_operations SET state=2,terminal_code='candidate_started' " +
                "WHERE operation_id=$operation AND state=1 AND handoff_id=$handoff AND terminal_code IS NULL;";
            command.Parameters.AddWithValue("$operation", bound.Plan.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$handoff", commit.HandoffId.ToString("D"));
            if (command.ExecuteNonQuery() != 1) throw Invalid();
            command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$operation AND status='accepted' AND initial_journal_id=$journal;";
            command.Parameters.AddWithValue("$journal", bound.Plan.JournalId.ToString("D"));
            if (command.ExecuteNonQuery() != 1) throw Invalid();
            return true;
        });
        observer?.Reached(PayloadPersistencePoint.SqlProjectionCommitted, bound.Plan.JournalId, bound.Latest.Revision, JournalPhase.Committed);
        _ = _binding.Read(bound.Plan.JournalId, bound.PlanReference);
    }

    private static IOException Invalid() => new("maintenance_candidate_commit_unverified");
}
