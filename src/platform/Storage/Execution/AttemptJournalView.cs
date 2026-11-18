using System.Collections.Immutable;
using System.Text.Json;

namespace NexusPipeline.Platform.Storage.Execution;

internal readonly record struct AttemptJournalView(JsonElement Root, int? Limit = null,
    ImmutableArray<JsonElement> Selection = default)
{
    public static implicit operator AttemptJournalView(JsonElement root) => new(root);

    internal bool IsOriginal => Limit is null && Selection.IsDefault;
    internal bool IsProjected => Limit is null && !Selection.IsDefault;
    internal int RetainedSelectionBytes => Selection.IsDefault ? 0 : 32 + Selection.Length * 16;

    internal IEnumerable<JsonElement> Participants
    {
        get
        {
            IEnumerable<JsonElement> source = Selection.IsDefault ? Root.GetProperty("participants").EnumerateArray() : Selection;
            return Limit is { } length ? source.Take(length) : source;
        }
    }
}
