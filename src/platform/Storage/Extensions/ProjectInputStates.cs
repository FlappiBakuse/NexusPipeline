using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal static class ProjectInputStates
{
    internal static ProjectInputState[] Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts, string provider, Guid binding)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id FROM extension_documents WHERE extension_id=$provider AND name=$name;";
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$name", ProjectChoicePublication.Name(binding));
        if (command.ExecuteScalar() is not string operation) return [];
        var reference = SecretDocumentRows.Find(connection, null, Guid.ParseExact(operation, "D")) ?? throw new IOException("project_secret_state_unbound");
        var source = new SecretDocumentFormat(instance, contracts).Read(reference);
        if (source.Document.ProjectChoice?.Change.Address != ProjectChoiceStore.Address(binding) || source.Document.ExtensionId != provider ||
            source.Document.Name != ProjectChoicePublication.Name(binding) ||
            new SecretDocumentPublisher(instance, database, contracts).Status(source) != "completed")
            throw new IOException("project_secret_state_unbound");
        var store = new SecretFieldStore(instance, database);
        return source.Document.Secrets.Select(input =>
        {
            if (input.SubjectId != binding) throw new IOException("project_secret_state_unbound");
            var state = store.GetStateAsync(new(provider, binding, input.Field), default).GetAwaiter().GetResult();
            return new ProjectInputState(input.Field, state.Revision, state.IsConfigured, state.IsReadable);
        }).ToArray();
    }
}
