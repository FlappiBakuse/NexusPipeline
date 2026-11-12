using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record CompiledProjectChoice(ConfigurationReadStamp Source, BlobReference Snapshot, BlobReference Content);

internal sealed class ProjectChoiceStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal const string ResourceId = "$project-choice";
    internal static ConfigurationAddress Address(Guid bindingId) => new(bindingId, ResourceId);

    [SupportedOSPlatform("windows")]
    internal ConfigurationSaveResult? Replay(Guid operationId, string subject, Guid bindingId, long bindingRevision,
        long expectedRevision, string extensionId, JsonElement choice, IReadOnlyList<ProjectInputIntent>? intents = null, string? providerId = null)
    {
        var digest = RequestDigest(bindingId, bindingRevision, expectedRevision, extensionId, choice, intents);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var reference = SecretDocumentRows.Find(connection, null, operationId);
        if (reference is null) return new ConfigurationRevisionStore(instance, database, contracts)
            .LookupSource(operationId, subject, Address(bindingId), digest);
        var source = new SecretDocumentFormat(instance, contracts).Read(reference);
        if (source.Document.SubjectId != subject || providerId is not null && source.Document.ExtensionId != providerId ||
            source.Document.ProjectChoice?.Change is not { } change ||
            change.Address != Address(bindingId) || change.SourceRequestDigest != digest) throw new Core.Operations.OperationConflictException();
        if (new SecretDocumentPublisher(instance, database, contracts).Status(source) != "completed")
            throw new IOException("project_secret_publication_pending");
        return new ConfigurationRevisionStore(instance, database, contracts).LookupSource(ProjectChoicePublication.Child(operationId),
            subject, Address(bindingId), digest) is { } saved ? saved with { OperationId = operationId } : throw new IOException("project_secret_result_missing");
    }

    internal ConfigurationSaveResult Save(Guid operationId, string subject, long expectedRevision,
        CompiledProjectDocument project, Action requireCurrent, DateTimeOffset acceptedAt)
    {
        requireCurrent();
        var bytes = BoundedJson.Serialize(project.Selection, null, 65536);
        var content = new ImmutableBlobStore(instance, contracts).PersistContent(bytes, default);
        var tree = new FileTreeSnapshot(FileObjectKind.File, [new("", FileObjectKind.File, content.SizeBytes, content.Sha256)]);
        var change = new ConfigurationRevisionChange(Address(project.BindingId), expectedRevision, project.BindingRevision,
            Contract(project), tree, ConfigurationUpdateKind.Manual)
        { SourceRequestDigest = RequestDigest(project.BindingId, project.BindingRevision, expectedRevision, project.Consumer.Installed.ExtensionId, project.Selection) };
        var store = new ConfigurationRevisionStore(instance, database, contracts);
        using var prepared = store.Prepare(change);
        requireCurrent();
        return store.Commit(ConfigurationRevisionStore.Identity(operationId, subject, change), prepared, acceptedAt);
    }

    [SupportedOSPlatform("windows")]
    internal async ValueTask VerifyReplayAsync(SecretDocumentService service, Guid operationId, IReadOnlyList<ProjectProtectedEdit> edits,
        Action requireCurrent, CancellationToken cancellation)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var reference = SecretDocumentRows.Find(connection, null, operationId);
        if (reference is null) throw new Core.Operations.OperationConflictException();
        var source = new SecretDocumentFormat(instance, contracts).Read(reference);
        var changes = source.Document.Secrets.Select(change => new SecretDocumentEdit(change.SubjectId, change.Field,
            change.Before.Revision, change.Disposition, edits.SingleOrDefault(edit => edit.Intent.Key == change.Field)?.Value)).ToArray();
        await service.WriteAsync(operationId, source.Document.SubjectId, source.Document.ExtensionId, source.Document.Name,
            source.Document.ExpectedRevision, new SecretDocumentFormat(instance, contracts).ReadContent(source.Document), changes,
            requireCurrent, cancellation, projectChoice: source.Document.ProjectChoice).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    internal async ValueTask<ConfigurationSaveResult> SaveProtectedAsync(SecretDocumentService service, Guid operationId, string subject,
        long expectedRevision, CompiledProjectDocument project, IReadOnlyList<ProjectProtectedEdit> edits, Action requireCurrent,
        DateTimeOffset acceptedAt, CancellationToken cancellation)
    {
        var extension = project.Provider.Installed.ExtensionId;
        var required = project.ProtectedInputs.Select(input => input.Key).ToHashSet(StringComparer.Ordinal);
        if (edits.Any(edit => !required.Contains(edit.Intent.Key))) throw new IOException("project_protected_input_unmapped");
        var changes = new List<SecretDocumentEdit>();
        var fields = new SecretFieldStore(instance, database);
        foreach (var key in required.Order(StringComparer.Ordinal))
        {
            var edit = edits.SingleOrDefault(edit => edit.Intent.Key == key);
            var state = await fields.GetStateAsync(new(extension, project.BindingId, key), cancellation).ConfigureAwait(false);
            changes.Add(new(project.BindingId, key, edit?.Intent.ExpectedRevision ?? state.Revision,
                edit?.Intent.Disposition ?? Core.Secrets.SecretDisposition.Keep, edit?.Value));
        }
        var bytes = BoundedJson.Serialize(project.Selection, null, 65536);
        var content = new ImmutableBlobStore(instance, contracts).PersistContent(bytes, cancellation);
        var tree = new FileTreeSnapshot(FileObjectKind.File, [new("", FileObjectKind.File, content.SizeBytes, content.Sha256)]);
        var change = new ConfigurationRevisionChange(Address(project.BindingId), expectedRevision, project.BindingRevision,
            Contract(project), tree, ConfigurationUpdateKind.Manual)
        { SourceRequestDigest = RequestDigest(project.BindingId, project.BindingRevision, expectedRevision, project.Consumer.Installed.ExtensionId,
            project.Selection, edits.Select(edit => edit.Intent).ToArray()) };
        using var prepared = new ConfigurationRevisionStore(instance, database, contracts).Prepare(change);
        var publication = prepared.Use((frozen, manifest) => new ProjectChoicePublication(frozen, manifest, acceptedAt));
        long revision;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            revision = ExtensionDocumentRows.Read(connection, null, extension, ProjectChoicePublication.Name(project.BindingId))?.Revision ?? 0;
        await service.WriteAsync(operationId, subject, extension, ProjectChoicePublication.Name(project.BindingId), revision, bytes,
            changes, requireCurrent, cancellation, projectChoice: publication).ConfigureAwait(false);
        return Replay(operationId, subject, project.BindingId, project.BindingRevision, expectedRevision,
            project.Consumer.Installed.ExtensionId, project.Selection, edits.Select(edit => edit.Intent).ToArray())!;
    }

    internal static ConfigurationContractStamp Contract(CompiledProjectDocument project) => Contract(project.ScriptRevision,
        project.RootId, project.RootRevision, project.Consumer.Installed.Package.ManifestSha256,
        project.Provider.Installed.Package.ManifestSha256, project.CompilerId);

    internal static ConfigurationContractStamp Contract(long scriptRevision, Guid rootId, long rootRevision,
        string consumer, string provider, string compilerId) => new(1,
        Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new
        {
            format = "nexus.project-choice-contract", version = 1, ScriptRevision = scriptRevision, RootId = rootId, RootRevision = rootRevision,
            consumer, provider, CompilerId = compilerId
        }))), null, false);

    private static string RequestDigest(Guid bindingId, long bindingRevision, long expectedRevision, string extensionId, JsonElement choice,
        IReadOnlyList<ProjectInputIntent>? intents = null)
    {
        var ordinary = new { format = "nexus.project-choice-request", version = 1, bindingId, bindingRevision, expectedRevision, extensionId, choice };
        return Convert.ToHexStringLower(SHA256.HashData(intents is not { Count: > 0 } ? BoundedJson.Serialize(ordinary) :
            BoundedJson.Serialize(new { ordinary, protectedInputs = intents.OrderBy(item => item.Key, StringComparer.Ordinal) })));
    }
}
