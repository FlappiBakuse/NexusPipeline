using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Security.Cryptography;

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
        using var transaction = DatabaseConnections.BeginRead(connection);
        return OperationRows.ReadExisting(connection, transaction, operation) is null ? null : ReadResult(connection, transaction, operation.OperationId);
    }

    internal ConfigurationSaveResult? LookupSource(Guid operationId, string subject, ConfigurationAddress address, string sourceRequestDigest)
        => LookupSource(operationId, subject, new[] { address }, sourceRequestDigest);

    internal ConfigurationSaveResult? LookupSource(Guid operationId, string subject, IReadOnlyCollection<ConfigurationAddress> addresses, string sourceRequestDigest)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = DatabaseConnections.BeginRead(connection);
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
        using var transaction = DatabaseConnections.BeginRead(connection);
        var binding = ConfigurationRevisionRows.Binding(connection, transaction, address.BindingId);
        var result = ConfigurationRevisionRows.Current(connection, transaction, address, _codec);
        if (result is not null && (result.Revision.UserId != binding.UserId || result.Revision.ScriptId != binding.ScriptId))
            throw new IOException("configuration_binding_owner_changed");
        return result;
    }

    internal void RequirePatchPreviewCurrent(StoredConfigurationRevision source)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration);
        using var transaction = DatabaseConnections.BeginRead(connection);
        ConfigurationRevisionRows.RequirePatchPreviewCurrent(connection, transaction, source);
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

    internal IReadOnlyDictionary<string, long> ReadCurrentRevisions(Guid bindingId)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Configuration);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT resource_id,revision FROM configuration.configuration_current WHERE binding_id=$binding;";
        command.Parameters.AddWithValue("$binding", bindingId.ToString("D"));
        using var rows = command.ExecuteReader();
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        while (rows.Read()) result.Add(rows.GetString(0), rows.GetInt64(1));
        return result;
    }

    internal void RequireReadSet(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<ConfigurationReadStamp> readSet)
    {
        if (readSet.Count == 0) return;
        var bindings = new Dictionary<Guid, (long Revision, Guid UserId, Guid ScriptId)>();
        var resources = new Dictionary<ConfigurationAddress, ConfigurationReadFiles>();
        foreach (var item in readSet)
        {
            var bindingId = item.Address.BindingId;
            if (!bindings.TryGetValue(bindingId, out var binding))
            {
                binding = ConfigurationRevisionRows.Binding(connection, transaction, bindingId);
                bindings.Add(bindingId, binding);
            }
            if (!resources.TryGetValue(item.Address, out var files))
            {
                var current = ConfigurationRevisionRows.Current(connection, transaction, item.Address, _codec)?.Revision;
                if (current is null || current.UserId != binding.UserId || current.ScriptId != binding.ScriptId)
                    throw new ConfigurationException("configuration_read_set_changed");
                files = new(current);
                resources.Add(item.Address, files);
            }
            if (binding.Revision != item.ExpectedBindingRevision)
                throw new ConfigurationException("configuration_read_set_changed");
            _ = files.Require(item);
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
        using var transaction = DatabaseConnections.BeginRead(connection);
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

internal sealed record StoredConfigurationRevision(ConfigurationRevision Revision, BlobReference Manifest);

internal static class ConfigurationRevisionRows
{
    internal const string ReadQuery = "configuration.revision.select";

    internal static (long Revision, Guid UserId, Guid ScriptId) Binding(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision,user_id,automation_id FROM automation.bindings WHERE binding_id=$binding;";
        command.Parameters.AddWithValue("$binding", id.ToString("D"));
        using var timing = StartupProfile.Measure(ReadQuery);
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new ConfigurationException("configuration_binding_missing");
        return (row.GetInt64(0), Guid.Parse(row.GetString(1)), Guid.Parse(row.GetString(2)));
    }

    internal static StoredConfigurationRevision? Current(SqliteConnection connection, SqliteTransaction transaction,
        ConfigurationAddress address, ConfigurationSnapshotCodec codec)
    {
        using var command = KeyCommand(connection, transaction, address);
        command.CommandText = "SELECT revision FROM configuration.configuration_current WHERE binding_id=$binding AND resource_id=$resource;";
        long? revision;
        using (StartupProfile.Measure(ReadQuery)) revision = command.ExecuteScalar() as long?;
        if (revision is { } saved) return Read(connection, transaction, address, saved, codec);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration.configuration_revisions WHERE binding_id=$binding AND resource_id=$resource);";
        using (StartupProfile.Measure(ReadQuery))
            if ((long)command.ExecuteScalar()! != 0) throw new IOException("configuration_current_missing");
        return null;
    }

    internal static void RequirePatchPreviewCurrent(SqliteConnection connection, SqliteTransaction transaction,
        StoredConfigurationRevision source)
    {
        var saved = source.Revision;
        using var command = KeyCommand(connection, transaction, saved.Address);
        command.CommandText = """
            SELECT 1 FROM configuration.configuration_current c
            JOIN configuration.configuration_revisions r
              ON r.binding_id=c.binding_id AND r.resource_id=c.resource_id AND r.revision=c.revision
            JOIN automation.bindings b ON b.binding_id=c.binding_id
            WHERE c.binding_id=$binding AND c.resource_id=$resource AND c.revision=$revision
              AND b.user_id=$user AND b.automation_id=$script
              AND r.user_id=$user AND r.automation_id=$script
              AND r.contract_revision=$contractRevision AND r.contract_digest=$contractDigest
              AND r.instance_key IS $instanceKey AND r.requires_instance=$requiresInstance
              AND r.manifest_path=$manifestPath AND r.manifest_size=$manifestSize AND r.manifest_digest=$manifestDigest;
            """;
        command.Parameters.AddWithValue("$revision", saved.Revision);
        command.Parameters.AddWithValue("$user", saved.UserId.ToString("D"));
        command.Parameters.AddWithValue("$script", saved.ScriptId.ToString("D"));
        command.Parameters.AddWithValue("$contractRevision", saved.Contract.Revision);
        command.Parameters.AddWithValue("$contractDigest", saved.Contract.DefinitionSha256);
        command.Parameters.AddWithValue("$instanceKey", (object?)saved.Contract.InstanceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$requiresInstance", saved.Contract.RequiresInstance ? 1 : 0);
        command.Parameters.AddWithValue("$manifestPath", source.Manifest.RelativePath);
        command.Parameters.AddWithValue("$manifestSize", source.Manifest.SizeBytes);
        command.Parameters.AddWithValue("$manifestDigest", source.Manifest.Sha256);
        using var timing = StartupProfile.Measure(ReadQuery);
        if (command.ExecuteScalar() is null) throw new ConfigurationException("configuration_patch_batch_source_invalid");
    }

    internal static StoredConfigurationRevision Read(SqliteConnection connection, SqliteTransaction transaction,
        ConfigurationAddress address, long revision, ConfigurationSnapshotCodec codec)
    {
        ConfigurationContractStamp contract;
        BlobReference manifest;
        Guid user, script;
        using (var command = KeyCommand(connection, transaction, address))
        {
            command.CommandText = "SELECT user_id,automation_id,contract_revision,contract_digest,instance_key,requires_instance," +
                "manifest_path,manifest_size,manifest_digest FROM configuration.configuration_revisions WHERE binding_id=$binding AND resource_id=$resource AND revision=$revision;";
            command.Parameters.AddWithValue("$revision", revision);
            using var timing = StartupProfile.Measure(ReadQuery);
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new IOException("configuration_revision_missing");
            user = Guid.Parse(row.GetString(0)); script = Guid.Parse(row.GetString(1));
            contract = new(row.GetInt64(2), row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4), row.GetBoolean(5));
            manifest = new(row.GetString(6), row.GetInt64(7), row.GetString(8));
        }
        try { ConfigurationRevisionPolicy.ValidateContract(contract); }
        catch (ConfigurationException error) { throw new IOException("configuration_contract_invalid", error); }
        var tree = codec.Read(manifest);
        var source = new ConfigurationRevision(address, revision, user, script, contract, tree);
        RequireAssets(connection, transaction, source);
        return new(source, manifest);
    }

    internal static void RequireCurrentFrozen(SqliteConnection connection, SqliteTransaction transaction,
        ConfigurationRevision source, long bindingRevision, BlobReference manifest) =>
        RequireFrozen(connection, transaction, source, bindingRevision, manifest, requireCurrent: true);

    internal static void RequireHistoricalFrozen(SqliteConnection connection, SqliteTransaction transaction,
        ConfigurationRevision source, long bindingRevision, BlobReference sourceManifest, StoredConfigurationRevision publication)
    {
        if (publication.Revision.Address != source.Address || publication.Revision.Revision < source.Revision ||
            publication.Revision.UserId != source.UserId || publication.Revision.ScriptId != source.ScriptId ||
            publication.Revision.Contract != source.Contract) throw new IOException("configuration_retry_source_unbound");
        RequireFrozen(connection, transaction, source, bindingRevision, sourceManifest, requireCurrent: false);
        RequireFrozen(connection, transaction, publication.Revision, bindingRevision, publication.Manifest, requireCurrent: true);
    }

    private static void RequireFrozen(SqliteConnection connection, SqliteTransaction transaction,
        ConfigurationRevision source, long bindingRevision, BlobReference manifest, bool requireCurrent)
    {
        var binding = Binding(connection, transaction, source.Address.BindingId);
        if (binding.Revision != bindingRevision) throw new ConfigurationException("configuration_binding_revision_conflict");
        if (binding.UserId != source.UserId || binding.ScriptId != source.ScriptId) throw new IOException("configuration_binding_owner_changed");
        using var command = KeyCommand(connection, transaction, source.Address);
        command.CommandText = "SELECT r.revision,r.user_id,r.automation_id,r.contract_revision,r.contract_digest,r.instance_key,r.requires_instance," +
            "r.manifest_path,r.manifest_size,r.manifest_digest FROM configuration.configuration_revisions r " +
            "WHERE r.binding_id=$binding AND r.resource_id=$resource AND r.revision=$revision " +
            "AND ($current=0 OR EXISTS(SELECT 1 FROM configuration.configuration_current c WHERE c.binding_id=r.binding_id AND c.resource_id=r.resource_id AND c.revision=r.revision));";
        command.Parameters.AddWithValue("$revision", source.Revision);
        command.Parameters.AddWithValue("$current", requireCurrent);
        using (var row = command.ExecuteReader())
        {
            if (!row.Read() || row.GetInt64(0) != source.Revision) throw new ConfigurationException("configuration_revision_conflict");
            if (row.GetString(1) != source.UserId.ToString("D") || row.GetString(2) != source.ScriptId.ToString("D") ||
                row.GetInt64(3) != source.Contract.Revision || row.GetString(4) != source.Contract.DefinitionSha256 ||
                (row.IsDBNull(5) ? null : row.GetString(5)) != source.Contract.InstanceKey || row.GetBoolean(6) != source.Contract.RequiresInstance ||
                row.GetString(7) != manifest.RelativePath || row.GetInt64(8) != manifest.SizeBytes || row.GetString(9) != manifest.Sha256)
                throw new IOException("configuration_run_source_changed");
        }
        RequireAssets(connection, transaction, source);
    }

    private static void RequireAssets(SqliteConnection connection, SqliteTransaction transaction, ConfigurationRevision source)
    {
        var references = source.Tree.Entries.Where(entry => entry.Kind == FileObjectKind.File)
            .Select(entry => (entry.Sha256!, entry.SizeBytes, $"blobs/content/{entry.Sha256}.bin")).Distinct().OrderBy(entry => entry.Item1, StringComparer.Ordinal).ToArray();
        using var assets = KeyCommand(connection, transaction, source.Address);
        assets.CommandText = "SELECT a.digest,b.size_bytes,b.relative_path FROM configuration.configuration_revision_assets a JOIN content.blobs b ON b.digest=a.digest " +
            "WHERE binding_id=$binding AND resource_id=$resource AND revision=$revision ORDER BY a.digest;";
        assets.Parameters.AddWithValue("$revision", source.Revision);
        var actual = new List<(string, long, string)>();
        using (StartupProfile.Measure(ReadQuery))
        using (var row = assets.ExecuteReader()) while (row.Read()) actual.Add((row.GetString(0), row.GetInt64(1), row.GetString(2)));
        if (!references.SequenceEqual(actual) || source.UserId == Guid.Empty || source.ScriptId == Guid.Empty)
            throw new IOException("configuration_reference_closure_invalid");
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, ConfigurationRevision value, BlobReference manifest)
    {
        using var command = KeyCommand(connection, transaction, value.Address);
        command.CommandText = "INSERT INTO configuration.configuration_revisions VALUES($binding,$resource,$revision,$user,$script,$contractRevision," +
            "$contractDigest,$instance,$required,$path,$digest,$size);";
        command.Parameters.AddWithValue("$revision", value.Revision);
        command.Parameters.AddWithValue("$user", value.UserId.ToString("D"));
        command.Parameters.AddWithValue("$script", value.ScriptId.ToString("D"));
        command.Parameters.AddWithValue("$contractRevision", value.Contract.Revision);
        command.Parameters.AddWithValue("$contractDigest", value.Contract.DefinitionSha256);
        command.Parameters.AddWithValue("$instance", (object?)value.Contract.InstanceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$required", value.Contract.RequiresInstance);
        command.Parameters.AddWithValue("$path", manifest.RelativePath);
        command.Parameters.AddWithValue("$digest", manifest.Sha256);
        command.Parameters.AddWithValue("$size", manifest.SizeBytes);
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO configuration.configuration_current VALUES($binding,$resource,$revision) " +
            "ON CONFLICT(binding_id,resource_id) DO UPDATE SET revision=excluded.revision;";
        command.ExecuteNonQuery();
    }

    internal static void AttachAssets(SqliteConnection connection, SqliteTransaction transaction, ConfigurationRevision value)
    {
        foreach (var entry in value.Tree.Entries.Where(entry => entry.Kind == FileObjectKind.File).DistinctBy(entry => entry.Sha256))
        {
            using var command = KeyCommand(connection, transaction, value.Address);
            command.CommandText = "INSERT INTO content.blobs VALUES($digest,$size,$path) ON CONFLICT(digest) DO NOTHING;";
            command.Parameters.AddWithValue("$digest", entry.Sha256!);
            command.Parameters.AddWithValue("$size", entry.SizeBytes);
            command.Parameters.AddWithValue("$path", $"blobs/content/{entry.Sha256}.bin");
            command.ExecuteNonQuery();
            command.CommandText = "SELECT size_bytes,relative_path FROM content.blobs WHERE digest=$digest;";
            using (var row = command.ExecuteReader())
                if (!row.Read() || row.GetInt64(0) != entry.SizeBytes || row.GetString(1) != $"blobs/content/{entry.Sha256}.bin")
                    throw new IOException("configuration_blob_identity_conflict");
            command.CommandText = "INSERT INTO configuration.configuration_revision_assets VALUES($binding,$resource,$revision,$digest);";
            command.Parameters.AddWithValue("$revision", value.Revision);
            command.ExecuteNonQuery();
        }
    }

    internal static SqliteCommand KeyCommand(SqliteConnection connection, SqliteTransaction transaction, ConfigurationAddress address)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$binding", address.BindingId.ToString("D"));
        command.Parameters.AddWithValue("$resource", address.ResourceId);
        return command;
    }
}
