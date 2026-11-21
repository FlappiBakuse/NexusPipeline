using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ProjectEditorContext(Guid BindingId, long BindingRevision, string ExtensionId, string ProviderId,
    string CompilerId, string ScriptName, long SelectionRevision, JsonElement? Selection)
{
    public IReadOnlyList<ProjectInputState> ProtectedInputs { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDraft { get; init; }
}
internal sealed record ProjectBindingDraft(DraftBindingIdentity Owner, ConfigurationRevisionBatch? Batch, DraftProjectSecrets? Secrets = null,
    Guid? CandidateId = null);
internal sealed record ProjectInputState(string Key, long Revision, bool IsConfigured, bool IsReadable);
