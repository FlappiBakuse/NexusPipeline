using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class ConfigurationBatchInventory
{
    internal static void VerifyReadOnly(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        try
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Content);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT o.operation_id,o.operation_kind,o.status FROM main.operations o WHERE " +
                "o.operation_kind IN ('configuration.save-batch','configuration.patch-batch','configuration.import-batch') OR " +
                "EXISTS(SELECT 1 FROM configuration.configuration_batch_results h WHERE h.operation_id=o.operation_id) OR " +
                "EXISTS(SELECT 1 FROM configuration.configuration_batch_items i WHERE i.operation_id=o.operation_id) LIMIT 4097;";
            var operations = new List<(Guid Id, string Kind, string Status)>();
            using (var row = command.ExecuteReader())
                while (row.Read()) operations.Add((Guid.Parse(row.GetString(0)), row.GetString(1), row.GetString(2)));
            if (operations.Count > 4096) throw new IOException("configuration_batch_inventory_limit");
            var codec = new ConfigurationSnapshotCodec(new(instance, contracts));
            foreach (var operation in operations)
            {
                var result = BatchResultRows.Read(connection, transaction, operation.Id);
                if (operation.Status != "completed" || operation.Kind is not ("configuration.save-batch" or "configuration.patch-batch" or "configuration.import-batch" or
                    "configuration.edit.save" or "configuration.run.sync" or "configuration.run.finish") ||
                    (result.SourceRequestDigest is not null) != (operation.Kind is "configuration.patch-batch" or "configuration.import-batch"))
                    throw new IOException("configuration_batch_operation_unbound");
                foreach (var item in result.Items)
                {
                    var saved = ConfigurationRevisionRows.Read(connection, transaction, item.Address, item.Revision, codec).Revision;
                    using var owner = connection.CreateCommand(); owner.Transaction = transaction;
                    owner.CommandText = "SELECT user_id,automation_id FROM automation.bindings WHERE binding_id=$binding;";
                    owner.Parameters.AddWithValue("$binding", item.Address.BindingId.ToString("D"));
                    using var binding = owner.ExecuteReader();
                    if (binding.Read() && (saved.UserId != Guid.Parse(binding.GetString(0)) || saved.ScriptId != Guid.Parse(binding.GetString(1))))
                        throw new IOException("configuration_batch_owner_unbound");
                }
            }
        }
        catch (Exception error) when (error is ConfigurationException or ArgumentException or FormatException or InvalidOperationException or OverflowException)
        { throw new IOException("configuration_batch_metadata_unrecognized", error); }
    }
}
