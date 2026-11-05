using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class ConfigurationEditRows
{
    internal static ConfigurationEditStatus? Read(SqliteConnection connection, SqliteTransaction? transaction, Guid sessionId, string subject)
    {
        long revision, bindingRevision;
        ConfigurationEditPhase phase;
        Guid? completion;
        Guid binding;
        using (var command = Command(connection, transaction, sessionId))
        {
            command.CommandText = "SELECT s.revision,s.phase,s.expected_binding_revision,s.binding_id,s.completion_operation_id,o.subject_id " +
                "FROM configuration_edit_sessions s JOIN operations o ON o.operation_id=s.session_id WHERE s.session_id=$session;";
            using var row = command.ExecuteReader();
            if (!row.Read()) return null;
            if (row.GetString(5) != subject) throw new NexusPipeline.Core.Operations.OperationConflictException();
            revision = row.GetInt64(0); phase = (ConfigurationEditPhase)row.GetInt32(1); bindingRevision = row.GetInt64(2);
            binding = Guid.Parse(row.GetString(3)); completion = row.IsDBNull(4) ? null : Guid.Parse(row.GetString(4));
        }
        using var items = Command(connection, transaction, sessionId);
        items.CommandText = "SELECT resource_id,expected_revision,contract_revision,contract_digest,instance_key,requires_instance,journal_id," +
            "plan_path,plan_digest FROM configuration_edit_resources WHERE session_id=$session ORDER BY resource_id LIMIT 33;";
        var resources = ImmutableArray.CreateBuilder<ConfigurationEditResource>();
        using (var row = items.ExecuteReader())
            while (row.Read()) resources.Add(new(new(binding, row.GetString(0)), row.GetInt64(1),
                new(row.GetInt64(2), row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4), row.GetBoolean(5)),
                Guid.Parse(row.GetString(6)), row.GetString(7), row.GetString(8)));
        if (!Enum.IsDefined(phase) || resources.Count is < 1 or > 32 || (phase is ConfigurationEditPhase.Saved or ConfigurationEditPhase.Cancelled) != completion.HasValue)
            throw new IOException("configuration_edit_metadata_unrecognized");
        foreach (var resource in resources) ConfigurationRevisionPolicy.ValidateContract(resource.Contract);
        return new(sessionId, revision, phase, bindingRevision, resources.OrderBy(item => item.Address.ResourceId, StringComparer.Ordinal).ToImmutableArray(), completion, false);
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId,
        ConfigurationEditRequest request, ImmutableArray<ConfigurationEditResource> resources)
    {
        using var command = Command(connection, transaction, sessionId);
        command.CommandText = "INSERT INTO configuration_edit_sessions(session_id,revision,phase,binding_id,expected_binding_revision,completion_operation_id,source_request_digest) " +
            "VALUES($session,1,0,$binding,$bindingRevision,NULL,$source);";
        command.Parameters.AddWithValue("$binding", request.Targets[0].Address.BindingId.ToString("D"));
        command.Parameters.AddWithValue("$bindingRevision", request.ExpectedBindingRevision);
        command.Parameters.AddWithValue("$source", (object?)request.SourceRequestDigest ?? DBNull.Value);
        command.ExecuteNonQuery();
        foreach (var resource in resources)
        {
            using var item = Command(connection, transaction, sessionId);
            item.CommandText = "INSERT INTO configuration_edit_resources VALUES($session,$resource,$expected,$contractRevision,$contractDigest,$instance,$required,$journal,$path,$digest);";
            item.Parameters.AddWithValue("$resource", resource.Address.ResourceId);
            item.Parameters.AddWithValue("$expected", resource.ExpectedRevision);
            item.Parameters.AddWithValue("$contractRevision", resource.Contract.Revision);
            item.Parameters.AddWithValue("$contractDigest", resource.Contract.DefinitionSha256);
            item.Parameters.AddWithValue("$instance", (object?)resource.Contract.InstanceKey ?? DBNull.Value);
            item.Parameters.AddWithValue("$required", resource.Contract.RequiresInstance);
            item.Parameters.AddWithValue("$journal", resource.JournalId.ToString("D"));
            item.Parameters.AddWithValue("$path", resource.PlanPath); item.Parameters.AddWithValue("$digest", resource.PlanDigest);
            item.ExecuteNonQuery();
        }
    }

    internal static void Advance(SqliteConnection connection, SqliteTransaction transaction, ConfigurationEditStatus session,
        ConfigurationEditPhase next, Guid? completion = null)
    {
        using var command = Command(connection, transaction, session.SessionId);
        command.CommandText = "UPDATE configuration_edit_sessions SET revision=revision+1,phase=$next,completion_operation_id=$completion " +
            "WHERE session_id=$session AND revision=$expected AND phase=$before;";
        command.Parameters.AddWithValue("$next", (int)next); command.Parameters.AddWithValue("$before", (int)session.Phase);
        command.Parameters.AddWithValue("$expected", session.Revision); command.Parameters.AddWithValue("$completion", (object?)completion?.ToString("D") ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1) throw new ConfigurationException("configuration_edit_revision_conflict");
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, Guid sessionId)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D")); return command;
    }
}
