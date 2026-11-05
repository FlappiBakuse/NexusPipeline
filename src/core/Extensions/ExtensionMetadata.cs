using System.Collections.Immutable;

namespace NexusPipeline.Core.Extensions;

public sealed record ExtensionLocaleText(string DisplayName, string GameName, string Summary);
public sealed record ExtensionMetadata(string DefaultLocale, string DisplayName, string GameName, string Summary,
    string? Author, ImmutableArray<string> Tags, ImmutableDictionary<string, ExtensionLocaleText> Locales,
    DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt)
{
    public bool Equals(ExtensionMetadata? other) => other is not null && DefaultLocale == other.DefaultLocale &&
        DisplayName == other.DisplayName && GameName == other.GameName && Summary == other.Summary && Author == other.Author &&
        CreatedAt == other.CreatedAt && UpdatedAt == other.UpdatedAt && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal) &&
        Locales.Count == other.Locales.Count && Locales.All(pair => other.Locales.TryGetValue(pair.Key, out var text) && text == pair.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DefaultLocale); hash.Add(DisplayName); hash.Add(GameName); hash.Add(Summary); hash.Add(Author);
        hash.Add(CreatedAt); hash.Add(UpdatedAt);
        foreach (var tag in Tags) hash.Add(tag, StringComparer.Ordinal);
        foreach (var pair in Locales.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { hash.Add(pair.Key); hash.Add(pair.Value); }
        return hash.ToHashCode();
    }
}
