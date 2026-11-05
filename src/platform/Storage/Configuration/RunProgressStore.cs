using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class RunProgressStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal RunSyncResult? Lookup(Guid operationId, string subject, Guid sessionId, ConfigurationRunPhase? terminal = null)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id,operation_kind,status FROM operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) return null;
            if (row.GetString(0) != subject || row.GetString(1) != (terminal.HasValue ? "configuration.run.finish" : "configuration.run.sync") || row.GetString(2) != "completed")
                throw new OperationConflictException();
        }
        var session = ConfigurationRunStore.Required(connection, transaction, sessionId, subject);
        if (terminal.HasValue && session.Phase != terminal) throw new OperationConflictException();
        command.CommandText = terminal.HasValue ? "SELECT EXISTS(SELECT 1 FROM configuration_run_sessions WHERE session_id=$session AND completion_operation_id=$operation);" :
            "SELECT EXISTS(SELECT 1 FROM configuration_run_syncs WHERE session_id=$session AND operation_id=$operation);";
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        if ((long)command.ExecuteScalar()! != 1) throw new OperationConflictException();
        return ReadResult(connection, transaction, operationId, session);
    }

    internal RunSyncResult Publish(Guid operationId, string subject, ConfigurationRunProgress sample, ConfigurationRunPhase? terminal = null,
        SnapshotCompletionProjection? completion = null)
    {
        if (operationId == Guid.Empty || terminal.HasValue && terminal is not (ConfigurationRunPhase.Completed or ConfigurationRunPhase.Interrupted))
            throw new ArgumentException("configuration_run_progress_identity_invalid");
        var replay = Lookup(operationId, subject, sample.Session.SessionId, terminal);
        if (replay is not null) return replay;
        var batch = sample.Batch;
        if ((batch is null) != (sample.Prepared is null) || sample.Items.Length != sample.Session.Resources.Length ||
            !sample.Items.Select(item => item.Resource).SequenceEqual(sample.Session.Resources) || sample.Items.Any(item =>
                (item.Candidate is not null) != (item.ReasonCode == "configuration_sync_allowed") || item.Saved.Address != item.Resource.Address))
            throw new IOException("configuration_run_progress_unbound");
        using var scene = terminal.HasValue ? new ConfigurationSceneEvidence(instance, database, contracts).Hold(sample.Session.Resources.Select(item =>
            new ConfigurationSceneResource(item.OperationId, item.JournalId, JournalKind.ConfigExchange, item.Address, item.PlanPath, item.PlanDigest)).ToArray(), installed: false) : null;
        var identity = new OperationIdentity(operationId, subject, Digest(sample, terminal), terminal.HasValue ? "configuration.run.finish" : "configuration.run.sync");
        if (sample.Prepared is { } prepared)
        {
            if (prepared.InstanceId != instance.InstanceId || prepared.RuntimeRoot != instance.RuntimeRoot) throw new IOException("configuration_preparation_instance_unbound");
            return prepared.Use((frozen, manifests) => ConfigurationBatchStore.Identity(operationId, subject, frozen).RequestDigest !=
                ConfigurationBatchStore.Identity(operationId, subject, batch!).RequestDigest ? throw new IOException("configuration_run_progress_unbound") :
                Commit(identity, sample, terminal, frozen, manifests, completion));
        }
        return Commit(identity, sample, terminal, null, [], completion);
    }

    private RunSyncResult Commit(OperationIdentity operation, ConfigurationRunProgress sample, ConfigurationRunPhase? terminal,
        ConfigurationRevisionBatch? batch, IReadOnlyList<BlobReference> manifests, SnapshotCompletionProjection? completion) => database.Write((connection, transaction) =>
    {
        if (!OperationRows.Accept(connection, transaction, operation, DateTimeOffset.UtcNow).IsNew)
            return ReadResult(connection, transaction, operation.OperationId, ConfigurationRunStore.Required(connection, transaction, sample.Session.SessionId, operation.SubjectId));
        var session = ConfigurationRunStore.Required(connection, transaction, sample.Session.SessionId, operation.SubjectId);
        if (completion is not null) SnapshotCompletionCursor.Require(connection, transaction, operation, session, terminal, completion);
        var executed = RunStopEvidence.HasExecutedWriter(connection, transaction, session);
        if (session.Revision != sample.Session.Revision || session.SyncOrdinal != sample.Session.SyncOrdinal ||
            sample.WasExecuted != executed ||
            session.ClosedElapsedMilliseconds != sample.Session.ClosedElapsedMilliseconds || session.PrimaryJobName != sample.Session.PrimaryJobName ||
            terminal.HasValue && sample.ElapsedMilliseconds != session.ClosedElapsedMilliseconds ||
            !terminal.HasValue && session.ClosedElapsedMilliseconds.HasValue ||
            !session.Resources.SequenceEqual(sample.Session.Resources) || session.Phase != sample.Session.Phase ||
            session.Phase is not (ConfigurationRunPhase.Prepared or ConfigurationRunPhase.Running) || session.IsWriterAdmissionClosed != terminal.HasValue ||
            !terminal.HasValue && (session.Phase != ConfigurationRunPhase.Running || sample.ElapsedMilliseconds < 15000 || session.SyncOrdinal != 0))
            throw new ConfigurationException("configuration_run_revision_conflict");
        var binding = ConfigurationRevisionRows.Binding(connection, transaction, session.Resources[0].Address.BindingId);
        if (binding.Revision != session.ExpectedBindingRevision) throw new ConfigurationException("configuration_binding_revision_conflict");
        var snapshots = new ConfigurationSnapshotCodec(new(instance, contracts));
        foreach (var item in sample.Items)
        {
            var current = ConfigurationRevisionRows.Current(connection, transaction, item.Resource.Address, snapshots)?.Revision;
            if (current is null || current.Revision != item.Resource.CurrentRevision || current.UserId != item.Saved.UserId || current.ScriptId != item.Saved.ScriptId ||
                current.Contract != item.Resource.Contract || current.Tree.Shape != item.Saved.Tree.Shape || !current.Tree.Entries.SequenceEqual(item.Saved.Tree.Entries))
                throw new ConfigurationException("configuration_revision_conflict");
        }
        var published = batch is null ? null : new ConfigurationBatchStore(instance, database, contracts).PublishInside(connection, transaction, operation.OperationId, batch, manifests);
        foreach (var item in sample.Items)
        {
            var next = published?.Items.SingleOrDefault(value => value.Saved.Address == item.Resource.Address)?.Saved.Revision ?? item.Resource.CurrentRevision;
            using var command = ConfigurationRunRows.Command(connection, transaction, session.SessionId);
            command.CommandText = "INSERT INTO configuration_run_sync_items VALUES($operation,$resource,$previous,$current,$reason);";
            command.Parameters.AddWithValue("$operation", operation.OperationId.ToString("D")); command.Parameters.AddWithValue("$resource", item.Resource.Address.ResourceId);
            command.Parameters.AddWithValue("$previous", item.Resource.CurrentRevision); command.Parameters.AddWithValue("$current", next);
            command.Parameters.AddWithValue("$reason", item.ReasonCode); command.ExecuteNonQuery();
            command.CommandText = "UPDATE configuration_run_resources SET current_revision=$current WHERE session_id=$session AND resource_id=$resource AND current_revision=$previous;";
            if (command.ExecuteNonQuery() != 1) throw new ConfigurationException("configuration_run_revision_conflict");
        }
        var due = executed && (terminal.HasValue || sample.ElapsedMilliseconds >= 15000);
        using (var command = ConfigurationRunRows.Command(connection, transaction, session.SessionId))
        {
            command.Parameters.AddWithValue("$operation", operation.OperationId.ToString("D"));
            if (due)
            {
                command.CommandText = "INSERT INTO configuration_run_syncs VALUES($session,$ordinal,$operation,$elapsed);";
                command.Parameters.AddWithValue("$ordinal", checked(session.SyncOrdinal + 1)); command.Parameters.AddWithValue("$elapsed", sample.ElapsedMilliseconds); command.ExecuteNonQuery();
            }
            command.CommandText = "UPDATE configuration_run_sessions SET revision=revision+1,sync_ordinal=sync_ordinal+$due,phase=$phase,completion_operation_id=$completion WHERE session_id=$session;";
            command.Parameters.AddWithValue("$due", due ? 1 : 0); command.Parameters.AddWithValue("$phase", (int)(terminal ?? session.Phase));
            command.Parameters.AddWithValue("$completion", terminal.HasValue ? operation.OperationId.ToString("D") : DBNull.Value); command.ExecuteNonQuery();
            if (terminal.HasValue)
            {
                command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id IN (SELECT operation_id FROM configuration_run_resources WHERE session_id=$session) OR operation_id=$session;";
                command.ExecuteNonQuery();
            }
            command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation;"; command.ExecuteNonQuery();
        }
        var result = ReadResult(connection, transaction, operation.OperationId, ConfigurationRunStore.Required(connection, transaction, session.SessionId, operation.SubjectId));
        return result with { IsNew = true };
    });

    private static RunSyncResult ReadResult(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, ConfigurationRunStatus session)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT resource_id,previous_revision,current_revision,reason_code FROM configuration_run_sync_items WHERE operation_id=$operation ORDER BY resource_id LIMIT 33;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        var items = ImmutableArray.CreateBuilder<RunSyncItem>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) items.Add(new(new(session.Resources[0].Address.BindingId, rows.GetString(0)), rows.GetInt64(1), rows.GetInt64(2), rows.GetString(3)));
        var ordered = items.OrderBy(item => item.Address.ResourceId, StringComparer.Ordinal).ToImmutableArray();
        if (!ordered.Select(item => item.Address).SequenceEqual(session.Resources.Select(item => item.Address))) throw new IOException("configuration_run_progress_result_unbound");
        return new(operationId, session, ordered, false);
    }

    private static string Digest(ConfigurationRunProgress sample, ConfigurationRunPhase? terminal) => Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new
    {
        sample.Session.SessionId, sample.Session.OwnerOperationId, sample.Session.ParentJournalId, sample.Session.StepId,
        sample.Session.Revision, sample.Session.SyncOrdinal, sample.Session.PrimaryJobName, sample.ElapsedMilliseconds, sample.WasExecuted, Terminal = terminal,
        Items = sample.Items.Select(item => new { item.Resource, item.Candidate, item.ReasonCode }).ToArray()
    })));
}
