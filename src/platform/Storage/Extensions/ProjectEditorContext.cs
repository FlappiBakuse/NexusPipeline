using System.Text.Json;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ProjectEditorContext(Guid BindingId, long BindingRevision, string ExtensionId, string ProviderId,
    string CompilerId, string ScriptName, long SelectionRevision, JsonElement? Selection)
{
    public IReadOnlyList<ProjectInputState> ProtectedInputs { get; init; } = [];
}
internal sealed record ProjectInputState(string Key, long Revision, bool IsConfigured, bool IsReadable);
