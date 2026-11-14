using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationRevisionStore : IConfigurationRevisionStore
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ImmutableBlobStore _blobs;
    private readonly ConfigurationSnapshotCodec _codec;

    public ConfigurationRevisionStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        if (instance.InstanceId != database.InstanceId || instance.RuntimeRoot != database.RuntimeRoot)
            throw new IOException("configuration_store_instance_unbound");
        _instance = instance; _database = database; _blobs = new(instance, contracts); _codec = new(_blobs);
    }

    public static OperationIdentity Identity(Guid operationId, string subject, ConfigurationRevisionChange change) =>
        new(operationId, subject, Digest(ConfigurationRevisionPolicy.Freeze(change)), "configuration.save");

    public PreparedConfigurationRevision Prepare(ConfigurationRevisionChange change)
    {
        change = ConfigurationRevisionPolicy.Freeze(change);
        FileTreeArchive.ValidateArchive(new(change.Tree, [], change.Tree.Entries.Where(entry => entry.Kind == FileObjectKind.File)
            .Select(entry => new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!)).Distinct().ToArray()));
        var held = new List<IDisposable>();
        try
        {
            var manifest = _codec.Write(change.Tree);
            held.Add(StableDirectoryLease.Acquire(Path.Combine(_instance.RuntimeRoot, "blobs/plans")));
            if (change.Tree.Entries.Any(entry => entry.Kind == FileObjectKind.File))
                held.Add(StableDirectoryLease.Acquire(Path.Combine(_instance.RuntimeRoot, "blobs/content")));
            foreach (var entry in change.Tree.Entries.Where(entry => entry.Kind == FileObjectKind.File).DistinctBy(entry => (entry.Sha256, entry.SizeBytes)))
                held.Add(_blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!)));
            held.Add(_blobs.OpenVerified(manifest));
            return new(_instance, change, manifest, held);
        }
        catch { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); throw; }
    }

    public ConfigurationSaveResult Commit(OperationIdentity operation, ConfigurationRevisionChange change, DateTimeOffset acceptedAt)
    {
        var replay = Lookup(operation, change);
        if (replay is not null) return replay;
        using var prepared = Prepare(change);
        return Commit(operation, prepared, acceptedAt);
    }

    public ConfigurationSaveResult Commit(OperationIdentity operation, PreparedConfigurationRevision prepared, DateTimeOffset acceptedAt) =>
        prepared.Use((change, manifest) =>
        {
            if (prepared.InstanceId != _instance.InstanceId || prepared.RuntimeRoot != _instance.RuntimeRoot)
                throw new IOException("configuration_preparation_instance_unbound");
            RequireIdentity(operation, change);
            return _database.Write((connection, transaction) => CommitInside(connection, transaction, operation, change, manifest, acceptedAt));
        });

    internal ConfigurationSaveResult CommitInside(SqliteConnection connection, SqliteTransaction transaction, OperationIdentity operation,
        ConfigurationRevisionChange change, BlobReference manifest, DateTimeOffset acceptedAt)
    {
        RequireIdentity(operation, change);
        var accepted = OperationRows.Accept(connection, transaction, operation, acceptedAt);
        if (!accepted.IsNew) return ReadResult(connection, transaction, operation.OperationId);
        var (next, changed) = Publish(connection, transaction, change, manifest);
        using var command = ConfigurationRevisionRows.KeyCommand(connection, transaction, change.Address);
        command.CommandText = "INSERT INTO configuration.configuration_mutations VALUES($operation,$binding,$resource,$revision,$changed,$source);";
        command.Parameters.AddWithValue("$operation", operation.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$revision", next.Revision);
        command.Parameters.AddWithValue("$changed", changed);
        command.Parameters.AddWithValue("$source", (object?)change.SourceRequestDigest ?? DBNull.Value);
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$operation;";
        command.ExecuteNonQuery();
        return ReadResult(connection, transaction, operation.OperationId) with { IsNew = true };
    }

    internal (ConfigurationRevision Saved, bool Changed) Publish(SqliteConnection connection, SqliteTransaction transaction,
        ConfigurationRevisionChange change, BlobReference manifest)
    {
        var binding = ConfigurationRevisionRows.Binding(connection, transaction, change.Address.BindingId);
        if (binding.Revision != change.ExpectedBindingRevision) throw new ConfigurationException("configuration_binding_revision_conflict");
        var before = ConfigurationRevisionRows.Current(connection, transaction, change.Address, _codec)?.Revision;
        var next = ConfigurationRevisionPolicy.Next(before, change, binding.UserId, binding.ScriptId);
        var changed = before is null || change.Kind != ConfigurationUpdateKind.Repair ||
            !FileTreeDelta.Compare(before.Tree, next.Tree).Changes.IsEmpty;
        if (changed)
        {
            ConfigurationRevisionRows.Insert(connection, transaction, next, manifest);
            ConfigurationRevisionRows.AttachAssets(connection, transaction, next);
        }
        return (changed ? next : before!, changed);
    }

    public ConfigurationSaveResult? Lookup(OperationIdentity operation, ConfigurationRevisionChange change)
    {
        RequireIdentity(operation, ConfigurationRevisionPolicy.Freeze(change));
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        return OperationRows.ReadExisting(connection, transaction, operation) is null ? null : ReadResult(connection, transaction, operation.OperationId);
    }

    internal ConfigurationSaveResult? LookupSource(Guid operationId, string subject, ConfigurationAddress address, string sourceRequestDigest)
        => LookupSource(operationId, subject, new[] { address }, sourceRequestDigest);

    internal ConfigurationSaveResult? LookupSource(Guid operationId, string subject, IReadOnlyCollection<ConfigurationAddress> addresses, string sourceRequestDigest)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT o.subject_id,o.operation_kind,m.binding_id,m.resource_id,m.source_request_digest " +
            "FROM main.operations o LEFT JOIN configuration.configuration_mutations m ON m.operation_id=o.operation_id WHERE o.operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) return null;
            if (row.GetString(0) != subject || row.GetString(1) != "configuration.save" || row.IsDBNull(2) ||
                !addresses.Any(address => row.GetString(2) == address.BindingId.ToString("D") && row.GetString(3) == address.ResourceId) || row.IsDBNull(4) ||
                row.GetString(4) != sourceRequestDigest) throw new OperationConflictException();
        }
        return ReadResult(connection, transaction, operationId);
    }

    public ConfigurationRevision? Read(ConfigurationAddress address)
        => ReadStored(address)?.Revision;

    internal StoredConfigurationRevision? ReadStored(ConfigurationAddress address)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        var binding = ConfigurationRevisionRows.Binding(connection, transaction, address.BindingId);
        var result = ConfigurationRevisionRows.Current(connection, transaction, address, _codec);
        if (result is not null && (result.Revision.UserId != binding.UserId || result.Revision.ScriptId != binding.ScriptId))
            throw new IOException("configuration_binding_owner_changed");
        return result;
    }

    internal long? ReadCurrentRevision(ConfigurationAddress address)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision FROM configuration.configuration_current WHERE binding_id=$binding AND resource_id=$resource;";
        command.Parameters.AddWithValue("$binding", address.BindingId.ToString("D"));
        command.Parameters.AddWithValue("$resource", address.ResourceId);
        return command.ExecuteScalar() as long?;
    }

    internal void RequireReadSet(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<ConfigurationReadStamp> readSet)
    {
        foreach (var item in readSet)
        {
            var binding = ConfigurationRevisionRows.Binding(connection, transaction, item.Address.BindingId);
            var current = ConfigurationRevisionRows.Current(connection, transaction, item.Address, _codec)?.Revision;
            var entry = current?.Tree.Entries.SingleOrDefault(entry => entry.RelativePath == item.RelativePath && entry.Kind == FileObjectKind.File);
            if (binding.Revision != item.ExpectedBindingRevision || current?.Revision != item.ExpectedRevision ||
                current.Contract != item.Contract || current.UserId != binding.UserId || current.ScriptId != binding.ScriptId ||
                entry?.Sha256 != item.SourceSha256)
                throw new ConfigurationException("configuration_read_set_changed");
        }
    }

    public static void VerifyReadOnly(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        try { VerifyCurrent(instance, database, contracts); }
        catch (Exception error) when (error is ConfigurationException or FormatException or System.Text.Json.JsonException)
        { throw new IOException("configuration_metadata_unrecognized", error); }
    }

    private static void VerifyCurrent(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        var store = new ConfigurationRevisionStore(instance, database, contracts);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT binding_id,resource_id FROM configuration.configuration_current;";
        var addresses = new List<ConfigurationAddress>();
        using (var row = command.ExecuteReader()) while (row.Read()) addresses.Add(new(Guid.Parse(row.GetString(0)), row.GetString(1)));
        foreach (var address in addresses)
        {
            var binding = ConfigurationRevisionRows.Binding(connection, transaction, address.BindingId);
            var saved = ConfigurationRevisionRows.Current(connection, transaction, address, store._codec)!.Revision;
            if (saved.UserId != binding.UserId || saved.ScriptId != binding.ScriptId) throw new IOException("configuration_binding_owner_changed");
        }
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration.configuration_revisions r JOIN automation.bindings b ON b.binding_id=r.binding_id " +
            "WHERE NOT EXISTS(SELECT 1 FROM configuration.configuration_current c WHERE c.binding_id=r.binding_id AND c.resource_id=r.resource_id));";
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("configuration_current_missing");
    }

    private ConfigurationSaveResult ReadResult(SqliteConnection connection, SqliteTransaction transaction, Guid operationId)
    {
        ConfigurationAddress address;
        long revision;
        bool changed;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT binding_id,resource_id,revision,has_changes FROM configuration.configuration_mutations WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new IOException("configuration_operation_result_missing");
            address = new(Guid.Parse(row.GetString(0)), row.GetString(1)); revision = row.GetInt64(2);
            changed = row.GetBoolean(3);
        }
        return ReadSavedResult(connection, transaction, operationId, address, revision, changed);
    }

    internal ConfigurationSaveResult ReadSavedResult(SqliteConnection connection, SqliteTransaction transaction,
        Guid operationId, ConfigurationAddress address, long revision, bool changed)
    {
        var saved = ConfigurationRevisionRows.Read(connection, transaction, address, revision, _codec).Revision;
        var previous = !changed ? saved.Tree : revision == 1 ? new FileTreeSnapshot(FileObjectKind.Missing, [])
            : ConfigurationRevisionRows.Read(connection, transaction, address, revision - 1, _codec).Revision.Tree;
        return new(operationId, saved, FileTreeDelta.Compare(previous, saved.Tree), false) { HasChanges = changed };
    }

    private static void RequireIdentity(OperationIdentity operation, ConfigurationRevisionChange change)
    { if (operation.Kind != "configuration.save" || operation.RequestDigest != Digest(change)) throw new OperationConflictException(); }
    private static string Digest(ConfigurationRevisionChange change) => Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(change)));
}
