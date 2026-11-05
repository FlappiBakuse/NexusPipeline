using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.State;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ScriptProjectChoice(long Revision, long ScriptRevision, JsonElement Selection);

[SupportedOSPlatform("windows")]
internal sealed class ScriptChoiceStore(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ResourceLeaseManager resources)
{
    private const string Format = "nexus.project.script-choice";
    private sealed record Document(string Format, int Version, Guid ScriptId, long ScriptRevision, Guid RootId,
        string ConsumerId, string ProviderId, JsonElement Selection);

    internal ScriptProjectChoice? Read(SelectedProjectSource source, string providerId)
    {
        source.RequireOwner(instance, database, lease, resources);
        if (!source.IsScriptAuthority) throw new IOException("project_script_authority_required");
        ExtensionIdentifiers.Require(providerId);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var saved = ExtensionDocumentRows.Read(connection, null, providerId, Name(source.Script.Id));
        if (saved is null) return null;
        using var parsed = StrictJson.Parse(saved.Json);
        var value = JsonSerializer.Deserialize<Document>(parsed.RootElement) ?? throw Invalid();
        if (parsed.RootElement.EnumerateObject().Count() != 8 || value.Format != Format || value.Version != 1 ||
            value.ScriptId != source.Script.Id || value.RootId != source.Root.Id || value.ConsumerId != source.Script.ExtensionId ||
            value.ProviderId != providerId || value.ScriptRevision < 1 || value.Selection.ValueKind != JsonValueKind.Object)
            throw Invalid();
        source.RequireCurrent();
        return new(saved.Revision, value.ScriptRevision, value.Selection.Clone());
    }

    internal DocumentCommit Save(Guid operationId, string subject, long expectedRevision, SelectedProjectSource source,
        string providerId, JsonElement selection, Action requireCurrent)
    {
        source.RequireOwner(instance, database, lease, resources);
        if (!source.IsScriptAuthority) throw new IOException("project_script_authority_required");
        ExtensionIdentifiers.Require(providerId);
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || subject.Any(char.IsControl) ||
            expectedRevision is < 0 or >= ExtensionDocumentRows.MaximumRevision || selection.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("project_script_choice_invalid");
        using var choice = StrictJson.Parse(BoundedJson.Serialize(selection, null, 65536));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(Format, 1, source.Script.Id, source.Script.Revision,
            source.Root.Id, source.Script.ExtensionId!, providerId, choice.RootElement));
        var name = Name(source.Script.Id); var digest = ExtensionDocumentRows.Digest(bytes);
        var identity = Identity(operationId, subject, source.Script.Id, source.Script.Revision, expectedRevision,
            source.Script.ExtensionId!, providerId, choice.RootElement);
        using var root = resources.TryAcquire(operationId, [new("extension-data:" + providerId, ResourceClaimMode.Mutation)]).Lease
            ?? throw new IOException("project_script_choice_unavailable");
        return database.Write((connection, transaction) =>
        {
            requireCurrent(); source.RequireCurrent();
            return ExtensionDocumentRows.Write(connection, transaction, identity, providerId, name, expectedRevision, bytes, digest);
        });
    }

    internal DocumentCommit? ReadAccepted(Guid operationId, string subject, Guid scriptId, long scriptRevision,
        long expectedRevision, string extensionId, string providerId, JsonElement selection)
    {
        lease.VerifyHeld(instance);
        var identity = Identity(operationId, subject, scriptId, scriptRevision, expectedRevision, extensionId, providerId, selection);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        if (OperationRows.ReadExisting(connection, transaction, identity) is null) return null;
        return ExtensionDocumentRows.ReadCommit(connection, transaction, operationId, providerId, subject)
            ?? throw new IOException("project_script_choice_result_missing");
    }

    private static OperationIdentity Identity(Guid operationId, string subject, Guid scriptId, long scriptRevision,
        long expectedRevision, string extensionId, string providerId, JsonElement selection) => new(operationId, subject,
        ExtensionDocumentRows.Digest(BoundedJson.Serialize(new { format = Format, version = 1, scriptId, scriptRevision,
            expectedRevision, extensionId, providerId, selection }, null, 131072)), "extension.document.write");

    private static string Name(Guid scriptId) => "project-script-" + scriptId.ToString("N");
    private static IOException Invalid() => new("project_script_choice_unrecognized");
}
