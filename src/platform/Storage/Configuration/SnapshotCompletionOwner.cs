using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class SnapshotCompletionOwner(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease? lease = null)
{
    internal void Prepare(Guid operationId, string subject, ConfigurationRunStatus session, Action beforeAppend)
        => (lease ?? throw new IOException("runtime_exclusive_lease_required")).PublishExecution(() => PrepareCore(operationId, subject, session, beforeAppend));

    private void PrepareCore(Guid operationId, string subject, ConfigurationRunStatus session, Action beforeAppend)
    {
        lease!.VerifyHeld(instance);
        var groups = ConfigurationCompletionBinding.Read(instance, database, contracts, session.ParentJournalId);
        var current = groups.SingleOrDefault(item => item.SessionId == session.SessionId);
        if (current is null || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(current), JsonSerializer.SerializeToElement(session)))
            throw new ConfigurationException("configuration_run_revision_conflict");
        _ = new RunStopEvidence(instance, database, contracts).ReadRestored(session);
        var parent = new RecoveryJournalRegistry(database).ReadJournal(session.ParentJournalId);
        var bound = AttemptPreparedBinding.Read(instance, contracts, database, parent.JournalId);
        var intents = SnapshotIntentCodec.Read(JsonSerializer.SerializeToElement(bound.Document));
        var slots = Slots(session);
        var existing = intents.SingleOrDefault(item => item.SessionId == session.SessionId);
        var intended = new SnapshotCompletionIntent(SnapshotIntentCodec.Format, 1,
            session.OwnerOperationId, session.ParentJournalId, parent.PlanDigest, session.SessionId, operationId, subject, "interrupted",
            session.Revision, SnapshotCompletionFacts.Digest(instance, database, contracts, session), slots);
        if (existing is not null)
        {
            RequireSame(existing, intended);
            return;
        }
        if (intents.Any(item => item.CompletionOperationId == operationId)) throw new OperationConflictException();
        var document = bound.Document;
        document["revision"] = parent.Revision + 1;
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        document["participants"]!.AsArray().Add(SnapshotIntentCodec.Participant(intended));
        document["receipts"]!.AsArray().Add(new JsonObject
        {
            ["receiptId"] = operationId.ToString("D"), ["recordedAt"] = document["updatedAt"]!.GetValue<string>(),
            ["stepId"] = SnapshotIntentCodec.Step, ["effect"] = "completed",
            ["detailCode"] = SnapshotIntentCodec.Reason
        });
        var store = new JournalStore(instance, contracts);
        beforeAppend();
        store.Append(parent.JournalId, parent.Revision, JsonSerializer.SerializeToUtf8Bytes(document));
        new AttemptPreparedProjection(store, new(database), new(database), null).Synchronize(bound.Plan, false);
    }

    internal IReadOnlyList<SnapshotCompletionProjection> ReadPending(RecoveryRegistrySnapshot snapshot)
    {
        var result = new List<SnapshotCompletionProjection>();
        var operations = new HashSet<Guid>(); var sessions = new HashSet<Guid>();
        foreach (var parent in snapshot.Journals.Where(item => item.Kind == JournalKind.RunAttempt))
        {
            var bound = AttemptPreparedBinding.Read(instance, contracts, database, parent.JournalId);
            foreach (var intent in SnapshotIntentCodec.Read(JsonSerializer.SerializeToElement(bound.Document)))
            {
                if (!operations.Add(intent.CompletionOperationId) || !sessions.Add(intent.SessionId)) throw Invalid();
                var session = ReadVerifiedIntent(parent, intent);
                if (session.CompletionOperationId.HasValue) continue;
                _ = ConfigurationCompletionBinding.Read(instance, database, contracts, parent.JournalId);
                using var scene = HoldRestored(session);
                result.Add(new(parent.OperationId, parent.JournalId, parent.Revision, parent.PlanDigest, session.SessionId,
                    intent.CompletionOperationId, intent.SubjectId, ConfigurationRunPhase.Interrupted, session.Revision, intent.EvidenceDigest, intent.SlotKeys));
            }
        }
        return result.AsReadOnly();
    }

    internal ConfigurationRunStatus ReadVerifiedIntent(RegisteredJournal parent, SnapshotCompletionIntent intent)
    {
        var session = new ConfigurationRunStore(instance, database, contracts).Read(intent.SessionId, intent.SubjectId) ?? throw Invalid();
        var binding = ConfigurationRunBinding.Read(instance, database, contracts, session);
        if (parent.Kind != JournalKind.RunAttempt || parent.OperationId != intent.ParentOperationId || parent.JournalId != intent.ParentJournalId ||
            parent.PlanDigest != intent.ParentPlanDigest || binding.ParentJournal.JournalId != parent.JournalId ||
            binding.ParentJournal.Revision != parent.Revision || binding.ParentJournal.Phase != parent.Phase ||
            binding.ParentJournal.PlanDigest != parent.PlanDigest || session.OwnerOperationId != intent.ParentOperationId ||
            session.ParentJournalId != intent.ParentJournalId || !Slots(session).SequenceEqual(intent.SlotKeys) ||
            !session.IsWriterAdmissionClosed || session.ClosedElapsedMilliseconds is null) throw Invalid();
        RequireOperationBindings(parent, session, intent.SubjectId);
        _ = new RunStopEvidence(instance, database, contracts).ReadRestored(session);
        if (SnapshotCompletionFacts.Digest(instance, database, contracts, session) != intent.EvidenceDigest) throw Invalid();
        if (session.CompletionOperationId is { } completed)
        {
            if (completed != intent.CompletionOperationId || session.Phase != ConfigurationRunPhase.Interrupted ||
                session.Revision != intent.GroupRevision + 1 || new RunProgressStore(instance, database, contracts)
                    .Lookup(completed, intent.SubjectId, session.SessionId, ConfigurationRunPhase.Interrupted) is null) throw Invalid();
        }
        else if (parent.Phase is not (JournalPhase.Prepared or JournalPhase.Applied) || session.Revision != intent.GroupRevision ||
            session.Phase is not (ConfigurationRunPhase.Prepared or ConfigurationRunPhase.Running)) throw Invalid();
        return session;
    }

    private void RequireOperationBindings(RegisteredJournal parent, ConfigurationRunStatus session, string subject)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE operation_id=$parent AND subject_id=$subject AND status=$parentStatus) " +
            "AND EXISTS(SELECT 1 FROM operations WHERE operation_id=$session AND subject_id=$subject AND operation_kind='configuration.run.begin' AND status=$status);";
        command.Parameters.AddWithValue("$parent", parent.OperationId.ToString("D")); command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$parentStatus", parent.Phase == JournalPhase.Committed ? "completed" : "accepted");
        command.Parameters.AddWithValue("$session", session.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$status", session.Phase == ConfigurationRunPhase.Prepared ? "accepted" : "completed");
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
        foreach (var resource in session.Resources)
        {
            var physical = new JournalStore(instance, contracts).Read(resource.JournalId);
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM operation_journals j JOIN operations o ON o.operation_id=j.operation_id " +
                "WHERE j.journal_id=$journal AND j.operation_id=$child AND j.operation_kind='configExchange' AND j.phase='committed' " +
                "AND j.revision=$revision AND j.plan_path=$path AND j.plan_digest=$digest AND o.subject_id=$subject " +
                "AND o.operation_kind='configuration.run.resource' AND o.request_digest=$digest AND o.status=$childStatus);";
            command.Parameters.AddWithValue("$journal", resource.JournalId.ToString("D")); command.Parameters.AddWithValue("$child", resource.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$revision", physical.Revision); command.Parameters.AddWithValue("$path", resource.PlanPath);
            command.Parameters.AddWithValue("$digest", resource.PlanDigest);
            command.Parameters.AddWithValue("$childStatus", session.CompletionOperationId.HasValue ? "completed" : "accepted");
            if ((long)command.ExecuteScalar()! != 1) throw Invalid();
            foreach (var key in new[] { "$journal", "$child", "$revision", "$path", "$digest", "$childStatus" }) command.Parameters.RemoveAt(key);
        }
    }

    internal IDisposable HoldRestored(ConfigurationRunStatus session) => new ConfigurationSceneEvidence(instance, database, contracts).Hold(
        session.Resources.Select(resource => new ConfigurationSceneResource(resource.OperationId, resource.JournalId, JournalKind.ConfigExchange,
            resource.Address, resource.PlanPath, resource.PlanDigest)).ToArray(), installed: false);

    private static ImmutableArray<string> Slots(ConfigurationRunStatus session) => session.Resources.Select(resource =>
        ConfigurationRevisionService.ResourceKey(resource.Address)).Order(StringComparer.Ordinal).ToImmutableArray();

    private static void RequireSame(SnapshotCompletionIntent existing, SnapshotCompletionIntent intended)
    {
        if (existing.ParentOperationId != intended.ParentOperationId || existing.ParentJournalId != intended.ParentJournalId ||
            existing.ParentPlanDigest != intended.ParentPlanDigest || existing.CompletionOperationId != intended.CompletionOperationId ||
            existing.SubjectId != intended.SubjectId || existing.Terminal != intended.Terminal || existing.GroupRevision != intended.GroupRevision ||
            existing.EvidenceDigest != intended.EvidenceDigest || !existing.SlotKeys.SequenceEqual(intended.SlotKeys)) throw new OperationConflictException();
    }

    private static IOException Invalid() => new("configuration_snapshot_completion_evidence_unbound");
}
