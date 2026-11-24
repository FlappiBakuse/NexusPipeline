using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ProjectChoicePublication(ConfigurationRevisionChange Change, BlobReference Manifest, DateTimeOffset AcceptedAt)
{
    internal static Guid Child(Guid operation) => new(SHA256.HashData(operation.ToByteArray()).AsSpan(0, 16));
    internal static string Name(Guid binding) => "project-choice-" + binding.ToString("N");
}

[SupportedOSPlatform("windows")]
internal static class ProjectChoicePublisher
{
    internal static void Before(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        SqliteConnection connection, SqliteTransaction transaction, ProjectChoicePublication publication)
    {
        var change = publication.Change;
        var binding = ConfigurationRevisionRows.Binding(connection, transaction, change.Address.BindingId);
        if (binding.Revision != change.ExpectedBindingRevision) throw new ConfigurationException("configuration_binding_revision_conflict");
        var before = ConfigurationRevisionRows.Current(connection, transaction, change.Address,
            new ConfigurationSnapshotCodec(new(instance, contracts)))?.Revision;
        _ = ConfigurationRevisionPolicy.Next(before, change, binding.UserId, binding.ScriptId);
    }

    internal static void RequireCompleted(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        SqliteConnection connection, SqliteTransaction transaction, Guid operation, string subject, ProjectChoicePublication publication)
    {
        var child = ProjectChoicePublication.Child(operation); var change = publication.Change;
        if (OperationRows.ReadExisting(connection, transaction, ConfigurationRevisionStore.Identity(child, subject, change))?.Status != "completed")
            throw new IOException("project_secret_result_unbound");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT binding_id,resource_id,revision,has_changes,source_request_digest FROM configuration.configuration_mutations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", child.ToString("D"));
        using (var row = command.ExecuteReader())
            if (!row.Read() || row.GetString(0) != change.Address.BindingId.ToString("D") || row.GetString(1) != change.Address.ResourceId ||
                row.GetInt64(2) != change.ExpectedRevision + 1 || !row.GetBoolean(3) || row.GetString(4) != change.SourceRequestDigest)
                throw new IOException("project_secret_result_unbound");
        var saved = ConfigurationRevisionRows.Read(connection, transaction, change.Address, change.ExpectedRevision + 1,
            new ConfigurationSnapshotCodec(new(instance, contracts)));
        if (saved.Manifest != publication.Manifest) throw new IOException("project_secret_manifest_changed");
    }
    internal static void Validate(ProjectChoicePublication value, Guid operation, string name, BlobReference content)
    {
        var change = ConfigurationRevisionPolicy.Freeze(value.Change);
        if (name != ProjectChoicePublication.Name(change.Address.BindingId) || change.Address.ResourceId != ProjectChoiceStore.ResourceId ||
            change.Kind != ConfigurationUpdateKind.Manual || change.SourceRequestDigest is null ||
            change.Tree.Shape != FileObjectKind.File || change.Tree.Entries.Count != 1 || change.Tree.Entries[0] is not { RelativePath: "", Kind: FileObjectKind.File } entry ||
            entry.SizeBytes != content.SizeBytes || entry.Sha256 != content.Sha256 || ProjectChoicePublication.Child(operation) == operation ||
            value.Manifest.RelativePath != $"blobs/plans/{value.Manifest.Sha256}.json")
            throw new IOException("project_secret_publication_invalid");
    }

    internal static ConfigurationSaveResult Commit(RuntimeInstance instance, Sqlite.StateDatabase database, ContractReader contracts,
        SqliteConnection connection, SqliteTransaction transaction, Guid operation, string subject, ProjectChoicePublication publication)
    {
        var store = new ConfigurationRevisionStore(instance, database, contracts);
        using var prepared = store.Prepare(publication.Change);
        return prepared.Use((change, manifest) =>
        {
            if (manifest != publication.Manifest) throw new IOException("project_secret_manifest_changed");
            var child = ProjectChoicePublication.Child(operation);
            return store.CommitInside(connection, transaction, ConfigurationRevisionStore.Identity(child, subject, change), change, manifest,
                publication.AcceptedAt) with { OperationId = operation };
        });
    }
}
