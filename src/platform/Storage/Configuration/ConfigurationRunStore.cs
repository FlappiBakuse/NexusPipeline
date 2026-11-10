using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationRunStore
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly ConfigurationSnapshotCodec _snapshots;

    public ConfigurationRunStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        if (instance.InstanceId != database.InstanceId || instance.RuntimeRoot != database.RuntimeRoot) throw new IOException("configuration_store_instance_unbound");
        _instance = instance; _database = database; _contracts = contracts; _snapshots = new(new(instance, contracts));
    }

    public static OperationIdentity BeginIdentity(Guid sessionId, string subject, Guid parentJournalId, string stepId, PreparedConfigurationRun prepared) =>
        prepared.Use(resources => new OperationIdentity(sessionId, subject, Digest(prepared, parentJournalId, stepId, resources), "configuration.run.begin"));

    public ConfigurationRunStatus? Read(Guid sessionId, string subject)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return ConfigurationRunRows.Read(connection, transaction, sessionId, subject);
    }

    public ConfigurationRunStatus Begin(OperationIdentity operation, Guid parentJournalId, string stepId,
        PreparedConfigurationRun prepared, DateTimeOffset acceptedAt) => prepared.Use(resources =>
    {
        if (prepared.Instance != _instance || operation.OperationId == Guid.Empty || operation.OperationId == prepared.OwnerOperationId ||
            resources.Any(item => item.Plan.OperationId == operation.OperationId) || operation !=
            new OperationIdentity(operation.OperationId, operation.SubjectId, Digest(prepared, parentJournalId, stepId, resources), "configuration.run.begin"))
            throw new OperationConflictException();
        using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
            if (OperationRows.ReadExisting(connection, transaction, operation) is not null)
                return Required(connection, transaction, operation.OperationId, operation.SubjectId);
        var parent = RunBindingEvidence.ReadAdmission(_instance, _database, _contracts, parentJournalId, prepared.OwnerOperationId, stepId,
            resources.Select(item => item.Reference));
        var states = resources.ToDictionary(item => item.Address, item =>
            new RunStateCodec(_instance, _contracts, new(_instance, _contracts)).ReadStructure(item.Plan.RunState!));
        var initial = RunInitialSources.Read(_instance, _database, _contracts, states);
        RunBindingEvidence.RequireAdmissionSource(_instance, _database, _contracts, parent, stepId, states);
        return _database.Write((connection, transaction) =>
        {
            if (!OperationRows.Accept(connection, transaction, operation, acceptedAt).IsNew)
                return Required(connection, transaction, operation.OperationId, operation.SubjectId);
            RequireParent(connection, transaction, parent, operation.SubjectId);
            var binding = ConfigurationRevisionRows.Binding(connection, transaction, prepared.Request.Resources[0].Target.Address.BindingId);
            if (binding.Revision != prepared.Request.ExpectedBindingRevision) throw new ConfigurationException("configuration_binding_revision_conflict");
            foreach (var resource in resources)
            {
                var target = prepared.Request.Resources.Single(item => item.Target.Address == resource.Address).Target;
                var state = states[resource.Address];
                if (state.Source.Revision != target.ExpectedRevision || state.Source.Contract != target.Contract ||
                    state.Source.UserId != binding.UserId || state.Source.ScriptId != binding.ScriptId)
                    throw new IOException("configuration_run_source_changed");
                RunInitialSources.RequireInside(connection, transaction, state, initial[resource.Address]);
                var child = new OperationIdentity(resource.Plan.OperationId, operation.SubjectId, resource.Reference.Sha256, "configuration.run.resource");
                if (!OperationRows.Accept(connection, transaction, child, acceptedAt).IsNew) throw new IOException("configuration_run_child_already_accepted");
                var claims = PhysicalResourceAdmission.Merge(resource.Plan.PhysicalClaims.Concat([
                    new("automation:definitions", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference),
                    new($"configuration:script:{binding.ScriptId:D}", ResourceClaimMode.Mutation),
                    new(ConfigurationRevisionService.ResourceKey(resource.Address), ResourceClaimMode.Mutation)]));
                var journal = new RegisteredJournal(resource.Plan.JournalId, child.OperationId, JournalKind.ConfigExchange, JournalPhase.Prepared, 1,
                    resource.Reference.RelativePath, resource.Reference.Sha256, claims.Select(item => item.ResourceKey).ToArray());
                RecoveryJournalRegistry.RegisterInside(connection, transaction, journal,
                    ConfigurationFileEffects.Create(journal.JournalId, journal.PlanDigest), JournalWriterStore.CurrentWriter,
                    capacityParentJournalId: parentJournalId);
                JournalResourceClaims.Insert(connection, transaction, journal.JournalId, claims);
            }
            ConfigurationRunRows.Insert(connection, transaction, operation.OperationId, prepared.OwnerOperationId, parentJournalId, stepId,
                RunBindingEvidence.ReadOrdinal(_instance, _contracts, parentJournalId, stepId), prepared.Request, resources, states);
            return Required(connection, transaction, operation.OperationId, operation.SubjectId) with { IsNew = true };
        });
    });

    internal BoundConfigurationRun ReadBound(ConfigurationRunStatus session) => ConfigurationRunBinding.Read(_instance, _database, _contracts, session);

    public ConfigurationRunStatus MarkRunning(Guid sessionId, string subject, long expectedRevision)
    {
        var session = Read(sessionId, subject) ?? throw new ConfigurationException("configuration_run_missing");
        RequirePrepared(session, expectedRevision);
        _ = ReadBound(session);
        using var evidence = new ConfigurationSceneEvidence(_instance, _database, _contracts).Hold(session.Resources.Select(resource =>
            new ConfigurationSceneResource(resource.OperationId, resource.JournalId, JournalKind.ConfigExchange, resource.Address, resource.PlanPath, resource.PlanDigest)).ToArray(), installed: true);
        return _database.Write((connection, transaction) =>
        {
            session = Required(connection, transaction, sessionId, subject); RequirePrepared(session, expectedRevision);
            var binding = ConfigurationRevisionRows.Binding(connection, transaction, session.Resources[0].Address.BindingId);
            if (binding.Revision != session.ExpectedBindingRevision) throw new ConfigurationException("configuration_binding_revision_conflict");
            foreach (var resource in session.Resources)
            {
                var saved = ConfigurationRevisionRows.Current(connection, transaction, resource.Address, _snapshots)?.Revision;
                if (saved is null || saved.Revision != resource.ExpectedRevision) throw new ConfigurationException("configuration_revision_conflict");
                ConfigurationRevisionPolicy.RequireCurrentContract(saved, resource.Contract);
            }
            using var command = ConfigurationRunRows.Command(connection, transaction, sessionId);
            command.CommandText = "UPDATE configuration_run_sessions SET phase=1,entered_running=1,revision=revision+1 WHERE session_id=$session AND phase=0 AND writer_gate=0 AND revision=$revision;";
            command.Parameters.AddWithValue("$revision", expectedRevision);
            if (command.ExecuteNonQuery() != 1) throw new ConfigurationException("configuration_run_revision_conflict");
            command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$session;"; command.ExecuteNonQuery();
            return Required(connection, transaction, sessionId, subject) with { IsNew = true };
        });
    }

    internal static void RequirePrepared(ConfigurationRunStatus session, long revision)
    {
        if (session.Phase != ConfigurationRunPhase.Prepared || session.Revision != revision || session.IsWriterAdmissionClosed)
            throw new ConfigurationException("configuration_run_revision_conflict");
    }

    private static void RequireParent(SqliteConnection connection, SqliteTransaction transaction, RegisteredJournal parent, string subject)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operation_journals j JOIN operations o ON o.operation_id=j.operation_id " +
            "WHERE j.journal_id=$journal AND j.operation_id=$owner AND j.operation_kind='runAttempt' AND j.phase=$phase AND j.revision=$revision " +
            "AND j.plan_path=$path AND j.plan_digest=$digest AND o.subject_id=$subject AND o.status='accepted');";
        command.Parameters.AddWithValue("$journal", parent.JournalId.ToString("D")); command.Parameters.AddWithValue("$owner", parent.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$phase", parent.Phase.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$revision", parent.Revision); command.Parameters.AddWithValue("$path", parent.PlanPath);
        command.Parameters.AddWithValue("$digest", parent.PlanDigest); command.Parameters.AddWithValue("$subject", subject);
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("configuration_run_parent_admission_closed");
    }

    internal static ConfigurationRunStatus Required(SqliteConnection connection, SqliteTransaction? transaction, Guid id, string subject) =>
        ConfigurationRunRows.Read(connection, transaction, id, subject) ?? throw new IOException("configuration_run_missing");

    private static string Digest(PreparedConfigurationRun prepared, Guid parent, string step, IReadOnlyList<PreparedRunConfiguration> resources) =>
        Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new { ParentJournalId = parent, StepId = step,
            OwnerOperationId = prepared.OwnerOperationId, prepared.Request.ExpectedBindingRevision,
            Resources = resources.Select(item => new { item.Address, item.Plan.OperationId, item.Plan.JournalId, item.Reference }).ToArray() })));
}
