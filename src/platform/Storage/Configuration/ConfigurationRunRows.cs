using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class ConfigurationRunRows
{
    internal static ConfigurationRunStatus? Read(SqliteConnection connection, SqliteTransaction? transaction, Guid sessionId, string subject)
    {
        int invocation; Guid owner, parent, binding; string step; string? primaryJob; long revision, bindingRevision, ordinal; long? elapsed; Guid? completion; ConfigurationRunPhase phase; bool closed, started;
        using (var command = Command(connection, transaction, sessionId))
        {
            command.CommandText = "SELECT s.owner_operation_id,s.parent_journal_id,s.step_id,s.binding_id,s.expected_binding_revision,s.revision,s.phase,s.writer_gate,o.subject_id,s.sync_ordinal,s.completion_operation_id,s.entered_running,s.closed_elapsed_milliseconds,s.primary_job_name,s.invocation_ordinal " +
                "FROM configuration_run_sessions s JOIN operations o ON o.operation_id=s.session_id WHERE s.session_id=$session;";
            using var row = command.ExecuteReader();
            if (!row.Read()) return null;
            if (row.GetString(8) != subject) throw new NexusPipeline.Core.Operations.OperationConflictException();
            owner = Guid.Parse(row.GetString(0)); parent = Guid.Parse(row.GetString(1)); step = row.GetString(2); binding = Guid.Parse(row.GetString(3));
            bindingRevision = row.GetInt64(4); revision = row.GetInt64(5); phase = (ConfigurationRunPhase)row.GetInt32(6); closed = row.GetBoolean(7);
            ordinal = row.GetInt64(9); completion = row.IsDBNull(10) ? null : Guid.Parse(row.GetString(10));
            started = row.GetBoolean(11);
            elapsed = row.IsDBNull(12) ? null : row.GetInt64(12);
            primaryJob = row.IsDBNull(13) ? null : row.GetString(13); invocation = row.GetInt32(14);
        }
        using var items = Command(connection, transaction, sessionId);
        items.CommandText = "SELECT resource_id,operation_id,journal_id,expected_revision,current_revision,contract_revision,contract_digest,instance_key,requires_instance,plan_path,plan_digest " +
            "FROM configuration_run_resources WHERE session_id=$session ORDER BY resource_id LIMIT 33;";
        var resources = ImmutableArray.CreateBuilder<RunResourceStatus>();
        using (var row = items.ExecuteReader())
            while (row.Read()) resources.Add(new(new(binding, row.GetString(0)), Guid.Parse(row.GetString(1)), Guid.Parse(row.GetString(2)), row.GetInt64(3), row.GetInt64(4),
                new(row.GetInt64(5), row.GetString(6), row.IsDBNull(7) ? null : row.GetString(7), row.GetBoolean(8)), row.GetString(9), row.GetString(10)));
        if (invocation is < 1 or > 99 || sessionId == Guid.Empty || owner == Guid.Empty || parent == Guid.Empty || owner == sessionId || revision is < 1 or > 9007199254740991 ||
            bindingRevision is < 1 or > 9007199254740991 || ordinal < 0 || ordinal > 9007199254740990 || !Enum.IsDefined(phase) ||
            (phase is ConfigurationRunPhase.Completed or ConfigurationRunPhase.Interrupted) != completion.HasValue || completion == Guid.Empty ||
            completion.HasValue && (!closed || !elapsed.HasValue) || !closed && elapsed.HasValue || elapsed is < 0 or > 922337203685477 ||
            primaryJob is not null && (primaryJob.Length != 50 || !primaryJob.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(primaryJob[18..], "N", out var primaryId) || primaryId == Guid.Empty || primaryJob[18..] != primaryId.ToString("N")) ||
            !started && elapsed is > 0 || string.IsNullOrWhiteSpace(step) || step.Any(char.IsControl) ||
            resources.Count is < 1 or > 32 || resources.Any(item => item.OperationId == Guid.Empty || item.JournalId == Guid.Empty ||
                item.ExpectedRevision < 1 || item.CurrentRevision < item.ExpectedRevision || item.CurrentRevision > 9007199254740991 ||
                !FileTreePolicy.Digest(item.PlanDigest) || item.PlanPath != $"blobs/plans/{item.PlanDigest}.json"))
            throw new IOException("configuration_run_metadata_unrecognized");
        foreach (var resource in resources) ConfigurationRevisionPolicy.ValidateContract(resource.Contract);
        return new(sessionId, owner, parent, step, revision, phase, bindingRevision, closed,
            resources.OrderBy(item => item.Address.ResourceId, StringComparer.Ordinal).ToImmutableArray(), false)
            { InvocationOrdinal = invocation, SyncOrdinal = ordinal, CompletionOperationId = completion, EnteredRunning = started, ClosedElapsedMilliseconds = elapsed, PrimaryJobName = primaryJob };
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId, Guid owner, Guid parent, string step,
        int invocation, ConfigurationRunRequest request, IReadOnlyList<PreparedRunConfiguration> resources,
        IReadOnlyDictionary<ConfigurationAddress, FrozenRunState> states)
    {
        using var command = Command(connection, transaction, sessionId);
        command.CommandText = "INSERT INTO configuration_run_sessions(session_id,owner_operation_id,parent_journal_id,step_id,binding_id,expected_binding_revision," +
            "revision,phase,writer_gate,sync_ordinal,completion_operation_id,entered_running,closed_elapsed_milliseconds,primary_job_name,invocation_ordinal) " +
            "VALUES($session,$owner,$parent,$step,$binding,$bindingRevision,1,0,0,0,NULL,0,NULL,NULL,$invocation);";
        command.Parameters.AddWithValue("$owner", owner.ToString("D")); command.Parameters.AddWithValue("$parent", parent.ToString("D")); command.Parameters.AddWithValue("$step", step);
        command.Parameters.AddWithValue("$binding", request.Resources[0].Target.Address.BindingId.ToString("D"));
        command.Parameters.AddWithValue("$bindingRevision", request.ExpectedBindingRevision); command.Parameters.AddWithValue("$invocation", invocation); command.ExecuteNonQuery();
        foreach (var resource in resources)
        {
            var target = request.Resources.Single(item => item.Target.Address == resource.Address).Target;
            using var item = Command(connection, transaction, sessionId);
            item.CommandText = "INSERT INTO configuration_run_resources VALUES($session,$resource,$operation,$journal,$expected,$expected,$contractRevision,$contractDigest,$instance,$required,$path,$digest);";
            item.Parameters.AddWithValue("$resource", resource.Address.ResourceId); item.Parameters.AddWithValue("$operation", resource.Plan.OperationId.ToString("D"));
            item.Parameters.AddWithValue("$journal", resource.Plan.JournalId.ToString("D")); item.Parameters.AddWithValue("$expected", states[resource.Address].InitialPublicationRevision);
            item.Parameters.AddWithValue("$contractRevision", target.Contract.Revision); item.Parameters.AddWithValue("$contractDigest", target.Contract.DefinitionSha256);
            item.Parameters.AddWithValue("$instance", (object?)target.Contract.InstanceKey ?? DBNull.Value); item.Parameters.AddWithValue("$required", target.Contract.RequiresInstance);
            item.Parameters.AddWithValue("$path", resource.Reference.RelativePath); item.Parameters.AddWithValue("$digest", resource.Reference.Sha256); item.ExecuteNonQuery();
        }
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, Guid sessionId)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D")); return command;
    }
}
