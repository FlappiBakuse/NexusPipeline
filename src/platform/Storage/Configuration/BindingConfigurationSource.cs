using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class BindingConfigurationSource
{
    internal const string ResourceId = "$script-source";

    internal static ConfigurationContractStamp Contract(AutomationScript script) => new(1,
        Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new {
            script.Id, script.RootId, script.ExtensionId, script.Executable, script.AdapterResources
        }))), null, false);

    internal static AutomationCatalog Read(StateDatabase database, SqliteConnection connection, SqliteTransaction transaction, AutomationCatalog catalog)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT binding_id FROM configuration_current WHERE resource_id=$resource;";
        command.Parameters.AddWithValue("$resource", ResourceId);
        var ids = new HashSet<Guid>();
        using (var rows = command.ExecuteReader()) while (rows.Read()) ids.Add(Guid.Parse(rows.GetString(0)));
        if (ids.Count == 0) return catalog;
        var contracts = new ContractReader();
        var blobs = new ImmutableBlobStore(new(database.InstanceId, database.RuntimeRoot, default), contracts);
        var codec = new ConfigurationSnapshotCodec(blobs);
        return catalog with { Bindings = catalog.Bindings.Select(binding => {
            if (!ids.Contains(binding.Id)) return binding;
            var stored = ConfigurationRevisionRows.Current(connection, transaction, new(binding.Id, ResourceId), codec)
                ?? throw new IOException("configuration_binding_source_missing");
            var script = catalog.Scripts.Single(item => item.Id == binding.ScriptId);
            var saved = stored.Revision;
            ConfigurationRevisionPolicy.RequireCurrentContract(saved, Contract(script));
            if (saved.UserId != binding.UserId || saved.ScriptId != binding.ScriptId || saved.Tree.Shape != FileObjectKind.File ||
                saved.Tree.Entries.Count != 1) throw new IOException("configuration_binding_source_invalid");
            var entry = saved.Tree.Entries.Single();
            using var stream = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            var source = BindingSourceCodec.Read(contracts.Read<NexusPipeline.Platform.Serialization.Protocol.AutomationBindingSource>(bytes,
                "automation-binding-source.schema.json"));
            var next = binding with { Source = source };
            _ = Core.Automation.BindingScriptSource.Resolve(script, next);
            return next;
        }).ToImmutableArray() };
    }

    internal static ConfigurationRevisionChange Change(RuntimeInstance instance, ContractReader contracts, AutomationBinding binding,
        AutomationScript script, Core.Automation.BindingScriptSource source, string digest)
    {
        var content = new ImmutableBlobStore(instance, contracts).PersistContent(BoundedJson.Serialize(BindingSourceCodec.Write(source)), default);
        return new(new(binding.Id, ResourceId), 0, binding.Revision, Contract(script),
            new(FileObjectKind.File, [new("", FileObjectKind.File, content.SizeBytes, content.Sha256)]), ConfigurationUpdateKind.Manual)
            { SourceRequestDigest = digest };
    }
}
