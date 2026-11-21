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
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id FROM extensions.extension_documents WHERE extension_id=$provider AND name=$name;";
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$name", ProjectChoicePublication.Name(binding));
        var store = new SecretFieldStore(instance, database);
        if (command.ExecuteScalar() is not string operation)
        {
            using var published = connection.CreateCommand();
            published.CommandText = """
                SELECT f.field FROM system.secret_fields f WHERE f.owner_kind='extension' AND f.owner_id=$provider AND f.subject_id=$binding
                AND EXISTS (SELECT 1 FROM main.operations o WHERE o.operation_kind=$kind AND o.status='completed'
                AND o.subject_id='extension/' || f.owner_id || '/' || f.subject_id || '/' || f.field) ORDER BY f.field;
                """;
            published.Parameters.AddWithValue("$provider", provider);
            published.Parameters.AddWithValue("$binding", binding.ToString("D"));
            published.Parameters.AddWithValue("$kind", DraftProjectSecrets.PublicationKind);
            using var reader = published.ExecuteReader();
            var states = new List<ProjectInputState>();
            while (reader.Read()) states.Add(Read(reader.GetString(0)));
            return states.ToArray();
        }
        var reference = SecretDocumentRows.Find(connection, null, Guid.ParseExact(operation, "D")) ?? throw new IOException("project_secret_state_unbound");
        var source = new SecretDocumentFormat(instance, contracts).Read(reference);
        if (source.Document.ProjectChoice?.Change.Address != ProjectChoiceStore.Address(binding) || source.Document.ExtensionId != provider ||
            source.Document.Name != ProjectChoicePublication.Name(binding) ||
            new SecretDocumentPublisher(instance, database, contracts).Status(source) != "completed")
            throw new IOException("project_secret_state_unbound");
        return source.Document.Secrets.Select(input =>
        {
            if (input.SubjectId != binding) throw new IOException("project_secret_state_unbound");
            return Read(input.Field);
        }).ToArray();
        ProjectInputState Read(string name)
        {
            var state = store.GetStateAsync(new(provider, binding, name), default).GetAwaiter().GetResult();
            return new(name, state.Revision, state.IsConfigured, state.IsReadable);
        }
    }
}
