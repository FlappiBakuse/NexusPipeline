using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed class AutomationExportService(RuntimeInstance instance, ContractReader contracts, StateDatabase database)
{
    public PreparedAutomationExport Prepare(DateTimeOffset createdAt, Guid? templateScriptId = null)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("automation_export_instance_unbound");
        AutomationCatalog catalog;
        IReadOnlyList<ExportSource> sources;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            catalog = AutomationRows.Read(connection, transaction);
            var scripts = templateScriptId is { } selected ? catalog.Scripts.Where(value => value.Id == selected) : catalog.Scripts;
            sources = ReadSources(connection, transaction, scripts.Select(value => value.Id).ToArray());
            transaction.Commit();
        }
        var files = new Dictionary<Guid, FileStream>();
        try
        {
            var blobs = new ImmutableBlobStore(instance, contracts);
            var attachments = sources.Select(value => value.Attachment).ToArray();
            var bundle = new AutomationBundleCodec(contracts).Encode(catalog, createdAt, templateScriptId,
                attachments.Select(value => new AutomationImportAttachment(value.Id, value.PackagePath, value.SizeBytes, value.Sha256)).ToArray());
            foreach (var source in sources) files.Add(source.Attachment.Id, blobs.OpenVerified(source.Reference));
            return new(bundle, attachments, files);
        }
        catch { foreach (var file in files.Values) file.Dispose(); throw; }
    }

    private static IReadOnlyList<ExportSource> ReadSources(SqliteConnection connection, SqliteTransaction transaction, Guid[] scripts)
    {
        if (scripts.Length == 0) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = scripts.Select((script, ordinal) =>
        {
            var name = "$s" + ordinal;
            command.Parameters.AddWithValue(name, script.ToString("D"));
            return name;
        });
        command.CommandText = "SELECT DISTINCT a.asset_id,a.package_path,a.digest,b.size_bytes,b.relative_path FROM automation_assets a " +
            "JOIN automation_asset_owners o USING(asset_id) JOIN blobs b USING(digest) WHERE o.automation_id IN (" + string.Join(',', names) + ") ORDER BY a.asset_id LIMIT 257;";
        using var reader = command.ExecuteReader();
        var result = new List<ExportSource>();
        while (reader.Read())
        {
            if (result.Count == 256) throw new IOException("automation_export_attachment_limit");
            var id = Guid.Parse(reader.GetString(0));
            PhysicalPathPolicy.ValidateRelativePath(reader.GetString(1));
            if (reader.GetString(4) != $"blobs/content/{reader.GetString(2)}.bin" || reader.GetInt64(3) is < 0 or > 8_388_608)
                throw new IOException("automation_export_content_scope_unbound");
            result.Add(new(new(id, reader.GetString(1), reader.GetInt64(3), reader.GetString(2)), new(reader.GetString(4), reader.GetInt64(3), reader.GetString(2))));
        }
        var collisions = result.GroupBy(value => value.Attachment.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).SelectMany(group => group.Select(value => value.Attachment.Id)).ToHashSet();
        if (collisions.Count != 0)
            return result.Select(value => value with { Attachment = value.Attachment with { PackagePath = "attachments/" + value.Attachment.Id.ToString("D") + ".bin" } }).ToArray();
        return result.AsReadOnly();
    }

    private sealed record ExportSource(AutomationExportAttachment Attachment, BlobReference Reference);
}
