using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationEditStore
{
    internal const string DraftBeginKind = "configuration.edit.begin-draft";
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly ConfigurationSnapshotCodec _snapshots;
    private readonly ConfigurationBatchStore _batches;

    public ConfigurationEditStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("configuration_store_instance_unbound");
        _instance = instance; _database = database; _contracts = contracts;
        _snapshots = new(new(instance, contracts)); _batches = new(instance, database, contracts);
    }

    public static OperationIdentity BeginIdentity(Guid operationId, string subject, ConfigurationEditRequest request) =>
        new(operationId, subject, Digest(ConfigurationEditPolicy.Freeze(request)),
            request.DraftBinding is null ? "configuration.edit.begin" : DraftBeginKind);
    public static OperationIdentity SaveIdentity(Guid operationId, string subject, ConfigurationEditCompletion completion) =>
        new(operationId, subject, Digest(ConfigurationEditPolicy.Freeze(completion)), "configuration.edit.save");
    public static OperationIdentity CancelIdentity(Guid operationId, string subject, ConfigurationEditCancel cancel)
    {
        if (cancel is null) throw new ConfigurationException("configuration_edit_session_invalid");
        ConfigurationEditPolicy.ValidateSession(cancel.SessionId, cancel.ExpectedSessionRevision);
        return new(operationId, subject, Digest(cancel), "configuration.edit.cancel");
    }

    public PreparedConfigurationEdit Prepare(Guid sessionId, ConfigurationEditRequest request) =>
        new ConfigurationEditPreparation(_instance, _database, _contracts).Prepare(sessionId, request);

    internal PreparedConfigurationEdit Prepare(Guid sessionId, ConfigurationEditRequest request, AdapterEditHook? adapter,
        Func<ConfigurationTarget, FileTreeSnapshot, FileTreeSnapshot>? initialize = null) =>
        new ConfigurationEditPreparation(_instance, _database, _contracts, adapter).Prepare(sessionId, request, initialize);

    public ConfigurationEditStatus? Read(Guid sessionId, string subject)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var transaction = DatabaseConnections.BeginRead(connection);
        return ConfigurationEditRows.Read(connection, transaction, sessionId, subject);
    }

    internal ConfigurationEditStatus? ReadActiveBinding(Guid bindingId, string subject)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var transaction = DatabaseConnections.BeginRead(connection);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT s.session_id FROM configuration.configuration_edit_sessions s JOIN main.operations o " +
            "ON o.operation_id=s.session_id WHERE s.binding_id=$binding AND s.phase=1 AND o.subject_id=$subject;";
        command.Parameters.AddWithValue("$binding", bindingId.ToString("D"));
        command.Parameters.AddWithValue("$subject", subject);
        return command.ExecuteScalar() is string id ? ConfigurationEditRows.Read(connection, transaction, Guid.Parse(id), subject) : null;
    }

    public ConfigurationEditStatus? ReadAcceptedSource(Guid sessionId, string subject, string sourceDigest, DraftBindingIdentity? draft = null)
    {
        if (!FileTreePolicy.Digest(sourceDigest)) throw new ConfigurationException("configuration_source_request_invalid");
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = DatabaseConnections.BeginRead(connection);
        var status = ConfigurationEditRows.Read(connection, transaction, sessionId, subject);
        if (status is null) return null;
        if (status.IsDraft != (draft is not null)) throw new OperationConflictException();
        using var command = ConfigurationEditRows.Command(connection, transaction, sessionId);
        command.CommandText = "SELECT source_request_digest FROM configuration.configuration_edit_sessions WHERE session_id=$session;";
        if (command.ExecuteScalar() is not string saved || saved != sourceDigest) throw new OperationConflictException();
        var journals = new JournalStore(_instance, _contracts);
        ConfigurationEditorCommand? editor = null;
        var targets = ImmutableArray.CreateBuilder<ConfigurationTarget>();
        foreach (var resource in status.Resources)
        {
            var document = journals.Read(resource.JournalId);
            if (document.Root.GetProperty("operationId").GetGuid() != sessionId || document.Kind != JournalKind.ConfigEdit ||
                document.PlanDigest != resource.PlanDigest) throw new IOException("configuration_edit_source_unbound");
            var bound = ConfigurationJournalBinding.Read(_instance, document, _contracts, _database);
            if (bound.Plan.Editor is null || bound.Plan.EditPolicy is null || editor is not null &&
                !EditorCommandPolicy.Equal(editor, bound.Plan.Editor)) throw new IOException("configuration_edit_source_unbound");
            editor = bound.Plan.Editor;
            targets.Add(new(resource.Address, resource.ExpectedRevision, resource.Contract, bound.Plan.TargetPath)
                { WritePolicy = bound.Plan.EditPolicy });
        }
        var request = new ConfigurationEditRequest(status.ExpectedBindingRevision, targets.ToImmutable())
            { Editor = editor, SourceRequestDigest = sourceDigest, DraftBinding = draft };
        var identity = BeginIdentity(sessionId, subject, request);
        if (OperationRows.ReadExisting(connection, transaction, identity) is null) throw new IOException("configuration_edit_source_unbound");
        return status;
    }

    public ConfigurationEditStatus? LookupBegin(OperationIdentity operation, ConfigurationEditRequest request)
    {
        Require(operation, BeginIdentity(operation.OperationId, operation.SubjectId, request));
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var transaction = DatabaseConnections.BeginRead(connection);
        return OperationRows.ReadExisting(connection, transaction, operation) is null ? null : Required(connection, transaction, operation.OperationId, operation.SubjectId);
    }

    public ConfigurationEditStatus Begin(OperationIdentity operation, PreparedConfigurationEdit prepared, DateTimeOffset acceptedAt) =>
        prepared.Use((request, resources) =>
        {
            Require(operation, BeginIdentity(operation.OperationId, operation.SubjectId, request));
            if (prepared.Instance != _instance || prepared.SessionId != operation.OperationId)
                throw new IOException("configuration_edit_preparation_unbound");
            return _database.Write((connection, transaction) =>
            {
                if (!OperationRows.Accept(connection, transaction, operation, acceptedAt).IsNew)
                    return Required(connection, transaction, operation.OperationId, operation.SubjectId);
                var address = resources[0].Address;
                var scriptId = RequireEditBinding(connection, transaction, request, address.BindingId);
                using (var pending = ConfigurationRevisionRows.KeyCommand(connection, transaction, resources[0].Address))
                {
                    pending.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration.configuration_edit_sessions WHERE binding_id=$binding AND phase IN (0,1));";
                    if ((long)pending.ExecuteScalar()! != 0) throw new ConfigurationException("configuration_edit_already_open");
                }
                foreach (var resource in resources)
                {
                    var saved = ConfigurationRevisionRows.Current(connection, transaction, resource.Address, _snapshots)?.Revision;
                    if ((saved?.Revision ?? 0) != resource.ExpectedRevision) throw new ConfigurationException("configuration_revision_conflict");
                    if (saved is not null) ConfigurationRevisionPolicy.RequireCurrentContract(saved, resource.Contract);
                    var physical = prepared.PhysicalClaims(resource.JournalId);
                    var logical = new[] { "automation:definitions", $"configuration:script:{scriptId:D}", ConfigurationRevisionService.ResourceKey(resource.Address) };
                    var keys = logical.Concat(physical.Select(item => item.ResourceKey)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    var journal = new RegisteredJournal(resource.JournalId, operation.OperationId, JournalKind.ConfigEdit, JournalPhase.Prepared, 1,
                        resource.PlanPath, resource.PlanDigest, keys);
                    RecoveryJournalRegistry.RegisterInside(connection, transaction, journal, ConfigurationFileEffects.Create(resource.JournalId, resource.PlanDigest), JournalWriterStore.CurrentWriter);
                    if (request.Editor is not null)
                        JournalResourceClaims.Insert(connection, transaction, resource.JournalId, keys.Select(key => new ResourceClaim(key,
                            physical.FirstOrDefault(item => item.ResourceKey == key)?.Mode ?? (key == "automation:definitions" ? ResourceClaimMode.Reference : ResourceClaimMode.Mutation))).ToArray());
                }
                ConfigurationEditRows.Insert(connection, transaction, operation.OperationId, request, resources);
                return Required(connection, transaction, operation.OperationId, operation.SubjectId) with { IsNew = true };
            });
        });

    private static Guid RequireEditBinding(SqliteConnection connection, SqliteTransaction transaction, ConfigurationEditRequest request, Guid id)
    {
        if (request.DraftBinding is not { } draft)
        {
            var binding = ConfigurationRevisionRows.Binding(connection, transaction, id);
            if (binding.Revision != request.ExpectedBindingRevision) throw new ConfigurationException("configuration_binding_revision_conflict");
            return binding.ScriptId;
        }
        DraftBindingOwner.Resolve(AutomationRows.Read(connection, transaction), id, draft);
        return draft.ScriptId;
    }

    public ConfigurationEditStatus MarkEditing(Guid sessionId, string subject, long expectedRevision)
    {
        ConfigurationEditPolicy.ValidateSession(sessionId, expectedRevision);
        var session = Read(sessionId, subject) ?? throw new ConfigurationException("configuration_edit_missing");
        RequireOpen(session, expectedRevision, ConfigurationEditPhase.Prepared);
        using var evidence = new ConfigurationEditEvidence(_instance, _database, _contracts).Hold(session, installed: true);
        return _database.Write((connection, transaction) =>
        {
            session = Required(connection, transaction, sessionId, subject);
            RequireOpen(session, expectedRevision, ConfigurationEditPhase.Prepared);
            RequireBaseline(connection, transaction, session);
            ConfigurationEditRows.Advance(connection, transaction, session, ConfigurationEditPhase.Editing);
            CompleteOperation(connection, transaction, sessionId);
            return Required(connection, transaction, sessionId, subject) with { IsNew = true };
        });
    }

    public ConfigurationBatchResult? LookupSave(OperationIdentity operation, ConfigurationEditCompletion completion)
    {
        completion = ConfigurationEditPolicy.Freeze(completion);
        Require(operation, SaveIdentity(operation.OperationId, operation.SubjectId, completion));
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = DatabaseConnections.BeginRead(connection);
        if (OperationRows.ReadExisting(connection, transaction, operation) is null) return null;
        RequireTerminal(Required(connection, transaction, completion.SessionId, operation.SubjectId), operation.OperationId, ConfigurationEditPhase.Saved);
        return _batches.ReadResult(connection, transaction, operation.OperationId, completion.Batch);
    }

    public ConfigurationBatchResult Save(OperationIdentity operation, ConfigurationEditCompletion completion, DateTimeOffset acceptedAt)
    {
        completion = ConfigurationEditPolicy.Freeze(completion);
        var replay = LookupSave(operation, completion);
        if (replay is not null) return replay;
        var session = Read(completion.SessionId, operation.SubjectId) ?? throw new ConfigurationException("configuration_edit_missing");
        ConfigurationEditPolicy.RequireCompletion(session, completion);
        using var prepared = _batches.Prepare(completion.Batch);
        using var evidence = new ConfigurationEditEvidence(_instance, _database, _contracts).Hold(session, installed: false, completion.Batch);
        return prepared.Use((batch, manifests) => _database.Write((connection, transaction) =>
        {
            if (!OperationRows.Accept(connection, transaction, operation, acceptedAt).IsNew)
            {
                RequireTerminal(Required(connection, transaction, session.SessionId, operation.SubjectId), operation.OperationId, ConfigurationEditPhase.Saved);
                return _batches.ReadResult(connection, transaction, operation.OperationId, batch);
            }
            session = Required(connection, transaction, session.SessionId, operation.SubjectId);
            ConfigurationEditPolicy.RequireCompletion(session, completion);
            var saved = _batches.PublishInside(connection, transaction, operation.OperationId, batch, manifests);
            ConfigurationEditRows.Advance(connection, transaction, session, ConfigurationEditPhase.Saved, operation.OperationId);
            CompleteOperation(connection, transaction, operation.OperationId);
            return saved;
        }));
    }

    internal static string ProjectedDigest(Guid sessionId, long revision, string? confirmedCandidateId) =>
        Digest(new { sessionId, revision, confirmedCandidateId });

    internal ConfigurationBatchResult? LookupProjectedSave(OperationIdentity operation, Guid sessionId)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = DatabaseConnections.BeginRead(connection);
        if (OperationRows.ReadExisting(connection, transaction, operation) is null) return null;
        RequireTerminal(Required(connection, transaction, sessionId, operation.SubjectId), operation.OperationId, ConfigurationEditPhase.Saved);
        return _batches.ReadResult(connection, transaction, operation.OperationId);
    }

    internal ConfigurationBatchResult PublishProjected(OperationIdentity operation, ConfigurationEditCompletion captured,
        ConfigurationRevisionBatch publication, DateTimeOffset acceptedAt)
    {
        var session = Read(captured.SessionId, operation.SubjectId) ?? throw new ConfigurationException("configuration_edit_missing");
        ConfigurationEditPolicy.RequireCompletion(session, captured);
        var bindingId = session.Resources[0].Address.BindingId;
        if (publication.Changes.Any(change => change.Address.BindingId != bindingId ||
            change.ExpectedBindingRevision != session.ExpectedBindingRevision ||
            change.Address.ResourceId.StartsWith("$editor:", StringComparison.Ordinal)) ||
            publication.Changes.Count(change => change.Address.ResourceId == BindingConfigurationSource.ResourceId) != 1)
            throw new ConfigurationException("configuration_edit_resources_changed");
        using var prepared = _batches.Prepare(publication);
        using var evidence = new ConfigurationEditEvidence(_instance, _database, _contracts).Hold(session, installed: false, captured.Batch);
        return prepared.Use((batch, manifests) => _database.Write((connection, transaction) =>
        {
            if (!OperationRows.Accept(connection, transaction, operation, acceptedAt).IsNew)
            {
                RequireTerminal(Required(connection, transaction, session.SessionId, operation.SubjectId), operation.OperationId, ConfigurationEditPhase.Saved);
                return _batches.ReadResult(connection, transaction, operation.OperationId, batch);
            }
            session = Required(connection, transaction, session.SessionId, operation.SubjectId);
            ConfigurationEditPolicy.RequireCompletion(session, captured);
            RequireBaseline(connection, transaction, session);
            var saved = _batches.PublishInside(connection, transaction, operation.OperationId, batch, manifests);
            ConfigurationEditRows.Advance(connection, transaction, session, ConfigurationEditPhase.Saved, operation.OperationId);
            CompleteOperation(connection, transaction, operation.OperationId);
            return saved;
        }));
    }

    public ConfigurationEditStatus? LookupCancel(OperationIdentity operation, ConfigurationEditCancel cancel)
    {
        Require(operation, CancelIdentity(operation.OperationId, operation.SubjectId, cancel));
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var transaction = DatabaseConnections.BeginRead(connection);
        if (OperationRows.ReadExisting(connection, transaction, operation) is null) return null;
        var session = Required(connection, transaction, cancel.SessionId, operation.SubjectId);
        RequireTerminal(session, operation.OperationId, ConfigurationEditPhase.Cancelled);
        return session;
    }

    public ConfigurationEditStatus Cancel(OperationIdentity operation, ConfigurationEditCancel cancel, DateTimeOffset acceptedAt)
    {
        var replay = LookupCancel(operation, cancel);
        if (replay is not null) return replay;
        var session = Read(cancel.SessionId, operation.SubjectId) ?? throw new ConfigurationException("configuration_edit_missing");
        RequireOpen(session, cancel.ExpectedSessionRevision);
        using var evidence = new ConfigurationEditEvidence(_instance, _database, _contracts).Hold(session, installed: false);
        return _database.Write((connection, transaction) =>
        {
            if (!OperationRows.Accept(connection, transaction, operation, acceptedAt).IsNew)
            {
                var original = Required(connection, transaction, session.SessionId, operation.SubjectId);
                RequireTerminal(original, operation.OperationId, ConfigurationEditPhase.Cancelled);
                return original;
            }
            session = Required(connection, transaction, session.SessionId, operation.SubjectId);
            RequireOpen(session, cancel.ExpectedSessionRevision);
            ConfigurationEditRows.Advance(connection, transaction, session, ConfigurationEditPhase.Cancelled, operation.OperationId);
            CompleteOperation(connection, transaction, operation.OperationId); CompleteOperation(connection, transaction, session.SessionId);
            return Required(connection, transaction, session.SessionId, operation.SubjectId) with { IsNew = true };
        });
    }

    internal static void RequireOpen(ConfigurationEditStatus session, long expectedRevision, ConfigurationEditPhase? phase = null)
    {
        if (session.Revision != expectedRevision || session.Phase is ConfigurationEditPhase.Saved or ConfigurationEditPhase.Cancelled ||
            phase.HasValue && session.Phase != phase.Value) throw new ConfigurationException("configuration_edit_revision_conflict");
    }

    private void RequireBaseline(SqliteConnection connection, SqliteTransaction transaction, ConfigurationEditStatus session)
    {
        var bindingId = session.Resources[0].Address.BindingId;
        if (session.IsDraft)
        {
            using var command = ConfigurationRevisionRows.KeyCommand(connection, transaction, session.Resources[0].Address);
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM automation.bindings WHERE binding_id=$binding);";
            if ((long)command.ExecuteScalar()! != 0) throw new ConfigurationException("configuration_draft_binding_changed");
        }
        else if (ConfigurationRevisionRows.Binding(connection, transaction, bindingId).Revision != session.ExpectedBindingRevision)
            throw new ConfigurationException("configuration_binding_revision_conflict");
        foreach (var resource in session.Resources)
        {
            var saved = ConfigurationRevisionRows.Current(connection, transaction, resource.Address, _snapshots)?.Revision;
            if ((saved?.Revision ?? 0) != resource.ExpectedRevision) throw new ConfigurationException("configuration_revision_conflict");
            if (saved is not null) ConfigurationRevisionPolicy.RequireCurrentContract(saved, resource.Contract);
        }
    }

    private static void RequireTerminal(ConfigurationEditStatus session, Guid operationId, ConfigurationEditPhase phase)
    {
        if (session.Phase != phase || session.CompletionOperationId != operationId) throw new IOException("configuration_edit_result_unrecognized");
    }

    private static ConfigurationEditStatus Required(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId, string subject) =>
        ConfigurationEditRows.Read(connection, transaction, sessionId, subject) ?? throw new IOException("configuration_edit_missing");
    private static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(value)));
    private static void Require(OperationIdentity operation, OperationIdentity expected)
    { if (operation != expected) throw new OperationConflictException(); }
    private static void CompleteOperation(SqliteConnection connection, SqliteTransaction transaction, Guid operationId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D")); command.ExecuteNonQuery();
    }
}

internal static class ConfigurationEditRows
{
    internal static ConfigurationEditStatus? Read(SqliteConnection connection, SqliteTransaction? transaction, Guid sessionId, string subject)
    {
        long revision, bindingRevision;
        ConfigurationEditPhase phase;
        Guid? completion;
        Guid binding;
        bool draft;
        using (var command = Command(connection, transaction, sessionId))
        {
            command.CommandText = "SELECT s.revision,s.phase,s.expected_binding_revision,s.binding_id,s.completion_operation_id,o.subject_id,o.operation_kind " +
                "FROM configuration.configuration_edit_sessions s JOIN main.operations o ON o.operation_id=s.session_id WHERE s.session_id=$session;";
            using var row = command.ExecuteReader();
            if (!row.Read()) return null;
            if (row.GetString(5) != subject) throw new NexusPipeline.Core.Operations.OperationConflictException();
            revision = row.GetInt64(0); phase = (ConfigurationEditPhase)row.GetInt32(1); bindingRevision = row.GetInt64(2);
            binding = Guid.Parse(row.GetString(3)); completion = row.IsDBNull(4) ? null : Guid.Parse(row.GetString(4));
            draft = row.GetString(6) == ConfigurationEditStore.DraftBeginKind;
        }
        using var items = Command(connection, transaction, sessionId);
        items.CommandText = "SELECT resource_id,expected_revision,contract_revision,contract_digest,instance_key,requires_instance,journal_id," +
            "plan_path,plan_digest FROM configuration.configuration_edit_resources WHERE session_id=$session ORDER BY resource_id LIMIT 33;";
        var resources = ImmutableArray.CreateBuilder<ConfigurationEditResource>();
        using (var row = items.ExecuteReader())
            while (row.Read()) resources.Add(new(new(binding, row.GetString(0)), row.GetInt64(1),
                new(row.GetInt64(2), row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4), row.GetBoolean(5)),
                Guid.Parse(row.GetString(6)), row.GetString(7), row.GetString(8)));
        if (!Enum.IsDefined(phase) || resources.Count is < 1 or > 32 || (phase is ConfigurationEditPhase.Saved or ConfigurationEditPhase.Cancelled) != completion.HasValue)
            throw new IOException("configuration_edit_metadata_unrecognized");
        foreach (var resource in resources) ConfigurationRevisionPolicy.ValidateContract(resource.Contract);
        return new(sessionId, revision, phase, bindingRevision, resources.OrderBy(item => item.Address.ResourceId, StringComparer.Ordinal).ToImmutableArray(), completion, false)
            { IsDraft = draft };
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId,
        ConfigurationEditRequest request, ImmutableArray<ConfigurationEditResource> resources)
    {
        using var command = Command(connection, transaction, sessionId);
        command.CommandText = "INSERT INTO configuration.configuration_edit_sessions(session_id,revision,phase,binding_id,expected_binding_revision,completion_operation_id,source_request_digest) " +
            "VALUES($session,1,0,$binding,$bindingRevision,NULL,$source);";
        command.Parameters.AddWithValue("$binding", request.Targets[0].Address.BindingId.ToString("D"));
        command.Parameters.AddWithValue("$bindingRevision", request.ExpectedBindingRevision);
        command.Parameters.AddWithValue("$source", (object?)request.SourceRequestDigest ?? DBNull.Value);
        command.ExecuteNonQuery();
        foreach (var resource in resources)
        {
            using var item = Command(connection, transaction, sessionId);
            item.CommandText = "INSERT INTO configuration.configuration_edit_resources VALUES($session,$resource,$expected,$contractRevision,$contractDigest,$instance,$required,$journal,$path,$digest);";
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
        command.CommandText = "UPDATE configuration.configuration_edit_sessions SET revision=revision+1,phase=$next,completion_operation_id=$completion " +
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
