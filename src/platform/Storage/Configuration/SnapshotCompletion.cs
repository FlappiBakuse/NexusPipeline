using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

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
        ConfigurationCompletionBinding.PrepareIntent(instance, database, contracts, parent, () =>
        {
            var slots = Slots(session);
            return new(SnapshotCompletionIntent.FormatId, 1, session.OwnerOperationId, session.ParentJournalId, parent.PlanDigest,
                session.SessionId, operationId, subject, "interrupted", session.Revision,
                SnapshotCompletionFacts.Digest(instance, database, contracts, session), slots);
        }, beforeAppend);
    }

    internal IReadOnlyList<SnapshotCompletionProjection> ReadPending(RecoveryRegistrySnapshot snapshot)
    {
        var result = new List<SnapshotCompletionProjection>();
        var operations = new HashSet<Guid>(); var sessions = new HashSet<Guid>();
        foreach (var parent in snapshot.Journals.Where(item => item.Kind == JournalKind.RunAttempt))
        {
            foreach (var intent in ConfigurationCompletionBinding.ReadIntents(instance, database, contracts, parent.JournalId))
            {
                if (!operations.Add(intent.CompletionOperationId) || !sessions.Add(intent.SessionId)) throw Invalid();
                var session = ReadVerifiedIntent(parent, intent);
                if (session.CompletionOperationId.HasValue) continue;
                _ = ConfigurationCompletionBinding.Read(instance, database, contracts, parent.JournalId);
                try
                {
                    using var scene = HoldRestored(session);
                    result.Add(new(parent.OperationId, parent.JournalId, parent.Revision, parent.PlanDigest, session.SessionId,
                        intent.CompletionOperationId, intent.SubjectId, ConfigurationRunPhase.Interrupted, session.Revision, intent.EvidenceDigest, intent.SlotKeys));
                }
                catch (Exception error) when (ScopedConfigurationRecovery.CanDeferScene(error)) { }
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
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operations WHERE operation_id=$parent AND subject_id=$subject AND status=$parentStatus) " +
            "AND EXISTS(SELECT 1 FROM main.operations WHERE operation_id=$session AND subject_id=$subject AND operation_kind='configuration.run.begin' AND status=$status);";
        command.Parameters.AddWithValue("$parent", parent.OperationId.ToString("D")); command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$parentStatus", parent.Phase == JournalPhase.Committed ? "completed" : "accepted");
        command.Parameters.AddWithValue("$session", session.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$status", session.Phase == ConfigurationRunPhase.Prepared ? "accepted" : "completed");
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
        foreach (var resource in session.Resources)
        {
            var physical = new JournalStore(instance, contracts).Read(resource.JournalId);
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operation_journals j JOIN main.operations o ON o.operation_id=j.operation_id " +
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

    private static IOException Invalid() => new("configuration_snapshot_completion_evidence_unbound");
}

internal static class SnapshotCompletionFacts
{
    internal static string Digest(RuntimeInstance instance, StateDatabase database, ContractReader contracts, ConfigurationRunStatus session)
    {
        var journals = new JournalStore(instance, contracts);
        var children = session.Resources.Select(resource => new
        {
            resource.Address, resource.OperationId, resource.JournalId, resource.ExpectedRevision, resource.Contract,
            resource.PlanPath, resource.PlanDigest,
            JournalDigest = journals.Read(resource.JournalId).Sha256
        }).ToArray();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var command = ConfigurationRunRows.Command(connection, null, session.SessionId);
        command.CommandText = "SELECT r.journal_id,c.writer_id,c.job_name,c.plan_path,c.plan_digest,c.plan_size " +
            "FROM configuration.configuration_run_resources r JOIN main.journal_writer_job_closures c ON c.journal_id=r.journal_id " +
            "WHERE r.session_id=$session ORDER BY r.resource_id LIMIT 33;";
        var closures = new List<object>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) closures.Add(new { JournalId = rows.GetString(0), WriterId = rows.GetString(1), JobName = rows.GetString(2),
                Path = rows.GetString(3), Digest = rows.GetString(4), Size = rows.GetInt64(5) });
        return Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new
        {
            session.SessionId, session.OwnerOperationId, session.ParentJournalId, session.StepId, session.ExpectedBindingRevision,
            session.IsWriterAdmissionClosed, session.EnteredRunning, session.ClosedElapsedMilliseconds, Children = children, Closures = closures
        })));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SnapshotCompletionPreflight(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal void Verify(IReadOnlyList<RegisteredJournal> registered)
    {
        try
        {
            var owner = new SnapshotCompletionOwner(instance, database, contracts);
            var operations = new HashSet<Guid>(); var sessions = new HashSet<Guid>();
            foreach (var parent in registered.Where(item => item.Kind == JournalKind.RunAttempt))
            {
                var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals", parent.JournalId.ToString("D")));
                if (!Directory.Exists(directory))
                {
                    if (File.Exists(directory) || parent.Phase != JournalPhase.Prepared || parent.Revision != 1)
                        throw new IOException("configuration_snapshot_completion_parent_missing");
                    continue;
                }
                foreach (var intent in ConfigurationCompletionBinding.ReadIntents(instance, database, contracts, parent.JournalId))
                {
                    if (!operations.Add(intent.CompletionOperationId) || !sessions.Add(intent.SessionId))
                        throw new IOException("configuration_snapshot_completion_intent_unbound");
                    // The recognized Prepared file prefix may lead SQL; verification must not project it or issue a permit.
                    _ = owner.ReadVerifiedIntent(parent, intent);
                }
            }
        }
        catch (Exception error) when (error is ConfigurationException or OperationConflictException or InvalidOperationException or ArgumentException)
        { throw new IOException("configuration_snapshot_completion_intent_unbound", error); }
    }
}

internal static class SnapshotCompletionCursor
{
    internal static void Require(SqliteConnection connection, SqliteTransaction transaction, OperationIdentity operation,
        ConfigurationRunStatus session, ConfigurationRunPhase? terminal, SnapshotCompletionProjection completion)
    {
        if (completion.CompletionOperationId != operation.OperationId || completion.SubjectId != operation.SubjectId ||
            completion.SessionId != session.SessionId || completion.ParentOperationId != session.OwnerOperationId ||
            completion.ParentJournalId != session.ParentJournalId || completion.GroupRevision != session.Revision ||
            completion.Terminal != terminal || !completion.SlotKeys.SequenceEqual(session.Resources.Select(item =>
                ConfigurationRevisionService.ResourceKey(item.Address)).Order(StringComparer.Ordinal)))
            throw new IOException("configuration_snapshot_completion_evidence_unbound");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operation_journals j JOIN main.journal_effect_gates g USING(journal_id) " +
            "JOIN main.operations o ON o.operation_id=j.operation_id WHERE j.journal_id=$parent AND j.operation_id=$owner " +
            "AND j.operation_kind='runAttempt' AND j.revision=$revision AND j.plan_digest=$digest " +
            "AND ((j.phase='prepared' AND g.state='sealed') OR (j.phase='applied' AND g.state='entered')) " +
            "AND g.revision=2 AND o.subject_id=$subject AND o.status='accepted');";
        command.Parameters.AddWithValue("$parent", completion.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$owner", completion.ParentOperationId.ToString("D"));
        command.Parameters.AddWithValue("$revision", completion.ParentRevision); command.Parameters.AddWithValue("$digest", completion.ParentPlanDigest);
        command.Parameters.AddWithValue("$subject", completion.SubjectId);
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("configuration_snapshot_completion_parent_changed");
        foreach (var resource in session.Resources)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operation_journals WHERE journal_id=$child AND operation_id=$operation " +
                "AND operation_kind='configExchange' AND phase='committed' AND plan_path=$path AND plan_digest=$plan);";
            command.Parameters.AddWithValue("$child", resource.JournalId.ToString("D")); command.Parameters.AddWithValue("$operation", resource.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$path", resource.PlanPath); command.Parameters.AddWithValue("$plan", resource.PlanDigest);
            if ((long)command.ExecuteScalar()! != 1) throw new IOException("configuration_snapshot_completion_child_changed");
            foreach (var key in new[] { "$child", "$operation", "$path", "$plan" }) command.Parameters.RemoveAt(key);
        }
    }
}
