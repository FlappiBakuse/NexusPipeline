using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace NexusPipeline.Core.Configuration;

public enum ConfigurationEditPhase { Prepared, Editing, Saved, Cancelled }
public sealed record ConfigurationEditRequest(long ExpectedBindingRevision, ImmutableArray<ConfigurationTarget> Targets)
{
    public ConfigurationEditorCommand? Editor { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceRequestDigest { get; init; }
}
public sealed record ConfigurationEditResource(ConfigurationAddress Address, long ExpectedRevision, ConfigurationContractStamp Contract,
    Guid JournalId, string PlanPath, string PlanDigest);
public sealed record ConfigurationEditStatus(Guid SessionId, long Revision, ConfigurationEditPhase Phase, long ExpectedBindingRevision,
    ImmutableArray<ConfigurationEditResource> Resources, Guid? CompletionOperationId, bool IsNew);
public sealed record ConfigurationEditCompletion(Guid SessionId, long ExpectedSessionRevision, ConfigurationRevisionBatch Batch);
public sealed record ConfigurationEditCancel(Guid SessionId, long ExpectedSessionRevision);

public static class ConfigurationEditPolicy
{
    public static ConfigurationEditRequest Freeze(ConfigurationEditRequest request)
    {
        if (request is null || request.ExpectedBindingRevision is < 1 or > 9007199254740991)
            throw new ConfigurationException("configuration_edit_request_invalid");
        if (request.SourceRequestDigest is not null && !FileTreePolicy.Digest(request.SourceRequestDigest))
            throw new ConfigurationException("configuration_source_request_invalid");
        return new(request.ExpectedBindingRevision, ConfigurationTargetPolicy.Freeze(request.Targets))
            { Editor = request.Editor is null ? null : EditorCommandPolicy.Freeze(request.Editor), SourceRequestDigest = request.SourceRequestDigest };
    }

    public static ConfigurationEditCompletion Freeze(ConfigurationEditCompletion completion)
    {
        ValidateSession(completion?.SessionId ?? Guid.Empty, completion?.ExpectedSessionRevision ?? 0);
        return completion! with { Batch = RevisionBatchPolicy.Freeze(completion!.Batch) };
    }

    public static void ValidateSession(Guid sessionId, long revision)
    {
        if (sessionId == Guid.Empty || revision is < 1 or >= 9007199254740991)
            throw new ConfigurationException("configuration_edit_session_invalid");
    }

    public static void RequireCompletion(ConfigurationEditStatus session, ConfigurationEditCompletion completion)
    {
        completion = Freeze(completion);
        if (session.SessionId != completion.SessionId || session.Revision != completion.ExpectedSessionRevision || session.Phase != ConfigurationEditPhase.Editing)
            throw new ConfigurationException("configuration_edit_revision_conflict");
        var changes = completion.Batch.Changes;
        if (session.Resources.Length != changes.Length) throw new ConfigurationException("configuration_edit_resources_changed");
        for (var index = 0; index < changes.Length; index++)
        {
            var resource = session.Resources[index];
            var change = changes[index];
            if (change.Address != resource.Address || change.ExpectedRevision != resource.ExpectedRevision ||
                change.ExpectedBindingRevision != session.ExpectedBindingRevision || change.Contract != resource.Contract ||
                change.Kind != ConfigurationUpdateKind.Manual || change.SourceRequestDigest is not null)
                throw new ConfigurationException("configuration_edit_resources_changed");
        }
    }
}
