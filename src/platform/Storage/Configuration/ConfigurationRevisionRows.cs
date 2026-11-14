using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record StoredConfigurationRevision(ConfigurationRevision Revision, BlobReference Manifest);

internal static class ConfigurationRevisionRows
{
    internal static (long Revision, Guid UserId, Guid ScriptId) Binding(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision,user_id,automation_id FROM automation.bindings WHERE binding_id=$binding;";
        command.Parameters.AddWithValue("$binding", id.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new ConfigurationException("configuration_binding_missing");
        return (row.GetInt64(0), Guid.Parse(row.GetString(1)), Guid.Parse(row.GetString(2)));
    }

    internal static StoredConfigurationRevision? Current(SqliteConnection connection, SqliteTransaction transaction,
        ConfigurationAddress address, ConfigurationSnapshotCodec codec)
    {
        using var command = KeyCommand(connection, transaction, address);
        command.CommandText = "SELECT revision FROM configuration.configuration_current WHERE binding_id=$binding AND resource_id=$resource;";
        if (command.ExecuteScalar() is long revision) return Read(connection, transaction, address, revision, codec);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration.configuration_revisions WHERE binding_id=$binding AND resource_id=$resource);";
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("configuration_current_missing");
        return null;
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
