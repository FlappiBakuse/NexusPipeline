using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationBatchStore
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ConfigurationRevisionStore _revisions;
    private readonly IBatchPersistenceObserver? _observer;

    public ConfigurationBatchStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        IBatchPersistenceObserver? observer = null)
    { _instance = instance; _database = database; _revisions = new(instance, database, contracts); _observer = observer; }

    public static OperationIdentity Identity(Guid operationId, string subject, ConfigurationRevisionBatch batch)
    {
        batch = RevisionBatchPolicy.Freeze(batch);
        var kind = SourceDigest(batch) is null ? "configuration.save-batch" :
            batch.Changes.All(change => change.Kind == ConfigurationUpdateKind.Manual) ? "configuration.import-batch" :
            batch.Changes.All(change => change.Kind == ConfigurationUpdateKind.Repair) ? "configuration.patch-batch" :
            throw new ConfigurationException("configuration_batch_source_invalid");
        return new(operationId, subject, Digest(batch), kind);
    }

    public PreparedConfigurationBatch Prepare(ConfigurationRevisionBatch batch)
    {
        batch = RevisionBatchPolicy.Freeze(batch);
        var items = new List<PreparedConfigurationRevision>();
        try
        {
            foreach (var change in batch.Changes) items.Add(_revisions.Prepare(change));
            return new(_instance, batch, items);
        }
        catch { for (var index = items.Count - 1; index >= 0; index--) items[index].Dispose(); throw; }
    }

    internal ConfigurationBatchResult? LookupImported(Guid operationId, string subject, long bindingRevision,
        IReadOnlyList<(ConfigurationAddress Address, long ExpectedRevision, string Digest)> sources)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id,operation_kind,status,request_digest FROM main.operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        string operationDigest;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) return null;
            if (row.GetString(0) != subject || row.GetString(1) != "configuration.import-batch" || row.GetString(2) != "completed")
                throw new OperationConflictException();
            operationDigest = row.GetString(3);
        }
        var stored = BatchResultRows.Read(connection, transaction, operationId);
        if (stored.SourceRequestDigest != SourceDigest(sources.Select(source => (source.Address, source.Digest)).ToArray()) ||
            !stored.Items.Select(item => item.Address).SequenceEqual(sources.Select(item => item.Address)))
            throw new OperationConflictException();
        var result = ReadResult(connection, transaction, operationId, stored);
        var batch = RevisionBatchPolicy.Freeze(new(sources.Select(source =>
        {
            var saved = result.Items.Single(item => item.Saved.Address == source.Address).Saved;
            return new ConfigurationRevisionChange(source.Address, source.ExpectedRevision, bindingRevision, saved.Contract,
                saved.Tree, ConfigurationUpdateKind.Manual) { SourceRequestDigest = source.Digest };
        }).ToImmutableArray()));
        RequireIdentity(new(operationId, subject, operationDigest, "configuration.import-batch"), batch);
        RequireResult(result, batch);
        return result;
    }

    public ConfigurationBatchResult? Lookup(OperationIdentity operation, ConfigurationRevisionBatch batch)
    {
        batch = RevisionBatchPolicy.Freeze(batch);
        RequireIdentity(operation, batch);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        return OperationRows.ReadExisting(connection, transaction, operation) is null ? null : ReadResult(connection, transaction, operation.OperationId, batch);
    }

    internal ConfigurationBatchResult? LookupSource(Guid operationId, string subject,
        IReadOnlyList<BatchSourceRequest> sources)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id,operation_kind,status,request_digest FROM main.operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        string operationDigest;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) return null;
            if (row.GetString(0) != subject || row.GetString(1) != "configuration.patch-batch" || row.GetString(2) != "completed")
                throw new OperationConflictException();
            operationDigest = row.GetString(3);
        }
        var stored = BatchResultRows.Read(connection, transaction, operationId);
        if (stored.SourceRequestDigest != SourceDigest(sources.Select(source => (source.Address, source.Digest)).ToArray()) ||
            !stored.Items.Select(item => item.Address).SequenceEqual(sources.Select(item => item.Address)))
            throw new OperationConflictException();
        var result = ReadResult(connection, transaction, operationId, stored);
        var batch = RevisionBatchPolicy.Freeze(new(sources.Select(source => new ConfigurationRevisionChange(source.Address,
            source.ExpectedRevision, source.ExpectedBindingRevision, source.Contract,
            result.Items.Single(item => item.Saved.Address == source.Address).Saved.Tree, ConfigurationUpdateKind.Repair)
            { SourceRequestDigest = source.Digest }).ToImmutableArray()));
        RequireIdentity(new(operationId, subject, operationDigest, "configuration.patch-batch"), batch);
        RequireResult(result, batch);
        return result;
    }

    public ConfigurationBatchResult Commit(OperationIdentity operation, PreparedConfigurationBatch prepared, DateTimeOffset acceptedAt) =>
        CommitChecked(operation, prepared, [], acceptedAt);

    internal ConfigurationBatchResult CommitChecked(OperationIdentity operation, PreparedConfigurationBatch prepared,
        IReadOnlyList<ConfigurationReadStamp> readSet, DateTimeOffset acceptedAt, Action? requireCurrent = null) =>
        prepared.Use((batch, manifests) =>
        {
            if (prepared.InstanceId != _instance.InstanceId || prepared.RuntimeRoot != _instance.RuntimeRoot)
                throw new IOException("configuration_preparation_instance_unbound");
            RequireIdentity(operation, batch);
            _observer?.Reached(BatchPersistencePoint.Prepared, operation.OperationId, -1);
            requireCurrent?.Invoke();
            var result = _database.Write((connection, transaction) =>
            {
                var accepted = OperationRows.Accept(connection, transaction, operation, acceptedAt);
                if (!accepted.IsNew) return ReadResult(connection, transaction, operation.OperationId, batch);
                _revisions.RequireReadSet(connection, transaction, readSet);
                _observer?.Reached(BatchPersistencePoint.OperationAccepted, operation.OperationId, -1);
                requireCurrent?.Invoke();
                var published = PublishInside(connection, transaction, operation.OperationId, batch, manifests);
                requireCurrent?.Invoke();
                using var complete = connection.CreateCommand();
                complete.Transaction = transaction;
                complete.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$operation;";
                complete.Parameters.AddWithValue("$operation", operation.OperationId.ToString("D"));
                complete.ExecuteNonQuery();
                return published;
            });
            if (result.IsNew) _observer?.Reached(BatchPersistencePoint.TransactionCommitted, operation.OperationId, -1);
            return result;
        });

    internal ConfigurationBatchResult PublishInside(SqliteConnection connection, SqliteTransaction transaction, Guid operationId,
        ConfigurationRevisionBatch batch, IReadOnlyList<BlobReference> manifests)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO configuration.configuration_batch_results(operation_id,resource_count,source_request_digest) VALUES($operation,$count,$source);";
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            command.Parameters.AddWithValue("$count", batch.Changes.Length);
            command.Parameters.AddWithValue("$source", (object?)SourceDigest(batch) ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        for (var index = 0; index < batch.Changes.Length; index++)
        {
            var (saved, changed) = _revisions.Publish(connection, transaction, batch.Changes[index], manifests[index]);
            using var command = ConfigurationRevisionRows.KeyCommand(connection, transaction, saved.Address);
            command.CommandText = "INSERT INTO configuration.configuration_batch_items(operation_id,ordinal,binding_id,resource_id,revision,has_changes) " +
                "VALUES($operation,$ordinal,$binding,$resource,$revision,$changed);";
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            command.Parameters.AddWithValue("$ordinal", index);
            command.Parameters.AddWithValue("$revision", saved.Revision);
            command.Parameters.AddWithValue("$changed", changed);
            command.ExecuteNonQuery();
            _observer?.Reached(BatchPersistencePoint.ItemPublished, operationId, index);
        }
        var published = ReadResult(connection, transaction, operationId, batch);
        return published with { IsNew = true, Items = published.Items.Select(item => item with { IsNew = true }).ToImmutableArray() };
    }

    internal ConfigurationBatchResult ReadResult(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, ConfigurationRevisionBatch batch)
    {
        var stored = BatchResultRows.Read(connection, transaction, operationId);
        if (stored.SourceRequestDigest != SourceDigest(batch) || !stored.Items.Select(row => row.Address).SequenceEqual(batch.Changes.Select(change => change.Address)))
            throw new IOException("configuration_batch_result_unrecognized");
        var result = ReadResult(connection, transaction, operationId, stored);
        RequireResult(result, batch);
        return result;
    }

    private ConfigurationBatchResult ReadResult(SqliteConnection connection, SqliteTransaction transaction, Guid operationId,
        BatchResultMetadata stored) => new(operationId, stored.Items.Select(row => _revisions.ReadSavedResult(connection, transaction,
            operationId, row.Address, row.Revision, row.Changed)).ToImmutableArray(), false);

    private static void RequireIdentity(OperationIdentity operation, ConfigurationRevisionBatch batch)
    { if (operation != Identity(operation.OperationId, operation.SubjectId, batch)) throw new OperationConflictException(); }

    private static void RequireResult(ConfigurationBatchResult result, ConfigurationRevisionBatch batch)
    {
        for (var index = 0; index < batch.Changes.Length; index++)
        {
            var change = batch.Changes[index]; var item = result.Items[index];
            if (item.Saved.Address != change.Address || item.Saved.Revision != change.ExpectedRevision + (item.HasChanges ? 1 : 0) ||
                !item.HasChanges && change.Kind != ConfigurationUpdateKind.Repair || item.Saved.Contract != change.Contract ||
                item.Saved.Tree.Shape != change.Tree.Shape || !item.Saved.Tree.Entries.SequenceEqual(change.Tree.Entries))
                throw new IOException("configuration_batch_result_unrecognized");
        }
    }
    private static string Digest(ConfigurationRevisionBatch batch) => Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(batch)));

    private static string? SourceDigest(ConfigurationRevisionBatch batch)
    {
        if (batch.Changes.All(change => change.SourceRequestDigest is null)) return null;
        if (batch.Changes.Any(change => change.SourceRequestDigest is null)) throw new ConfigurationException("configuration_batch_source_invalid");
        return SourceDigest(batch.Changes.Select(change => (change.Address, change.SourceRequestDigest!)).ToArray());
    }

    private static string SourceDigest(IReadOnlyList<(ConfigurationAddress Address, string Digest)> sources) =>
        Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(sources.Select(source => new { source.Address, source.Digest }).ToArray())));
}
