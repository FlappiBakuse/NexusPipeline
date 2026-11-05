using System.Text.Json;

namespace NexusPipeline.Platform.Storage.Execution;

internal readonly record struct DecodedExecutionParticipant<T>(T Value, bool MatchesOriginal);

internal static class ExecutionParticipantText
{
    private const int MaximumEntries = 256;
    private const int MaximumOriginalCharacters = 512 * 1024;
    private readonly record struct TextKey(Type Type, JsonSerializerOptions Options, string Original);
    private static readonly object Gate = new();
    private static readonly Dictionary<TextKey, object> Entries = [];
    private static readonly Queue<TextKey> Order = [];
    private static int _characters;

    internal static DecodedExecutionParticipant<T> Read<T>(JsonElement identity, JsonSerializerOptions options,
        Func<Exception> invalid) where T : class
    {
        if (typeof(T) != typeof(StepWriterIntent) && typeof(T) != typeof(ExecutionCaptureIntent) &&
            typeof(T) != typeof(AndroidCommandRegistration) && typeof(T) != typeof(AndroidStartupIntent) &&
            typeof(T) != typeof(AndroidCollectedIntent) && typeof(T) != typeof(PreviewDomainIntent) &&
            typeof(T) != typeof(AndroidRetentionIntent) && typeof(T) != typeof(TargetJobIntent) && typeof(T) != typeof(PcStageIntent))
            throw new ArgumentException("execution_participant_type_not_immutable");
        var key = new TextKey(typeof(T), options, identity.GetRawText());
        // Exact owned text and fixed serializer identity reuse only immutable decoding, never a binding or admission decision.
        lock (Gate)
            if (Entries.TryGetValue(key, out var cached)) return new((T)cached, true);
        var value = identity.Deserialize<T>(options) ?? throw invalid();
        var exact = JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(value, options));
        if (exact) Remember(key, value);
        return new(value, exact);
    }

    private static void Remember(TextKey key, object value)
    {
        if (key.Original.Length > MaximumOriginalCharacters) return;
        lock (Gate)
        {
            if (Entries.ContainsKey(key)) return;
            while (Entries.Count >= MaximumEntries || _characters + key.Original.Length > MaximumOriginalCharacters)
            {
                var oldest = Order.Dequeue();
                Entries.Remove(oldest); _characters -= oldest.Original.Length;
            }
            Entries.Add(key, value); Order.Enqueue(key); _characters += key.Original.Length;
        }
    }
}
