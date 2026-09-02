using System.Text;

namespace NexusPipeline.Services;

internal sealed record NameNormalizationChange(string EntityId, string OldName, string NewName);

internal static class EntityNameRules
{
    public static bool HasConflict<T>(
        IEnumerable<T> items,
        string candidateName,
        Func<T, string?> nameSelector,
        Func<T, bool>? ignore = null)
    {
        foreach (T item in items)
        {
            if (ignore?.Invoke(item) == true)
            {
                continue;
            }

            string? existingName = nameSelector(item);
            if (string.Equals(existingName, candidateName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<NameNormalizationChange> NormalizeDuplicates<T>(
        IList<T> items,
        Func<T, string?> nameSelector,
        Action<T, string> nameSetter,
        Func<T, string?> idSelector,
        Func<T, int> indexSelector,
        int maxBytes)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (T item in items)
        {
            string? name = nameSelector(item);
            if (!string.IsNullOrWhiteSpace(name))
            {
                reserved.Add(name);
            }
        }

        var changes = new List<NameNormalizationChange>();
        var ordered = items
            .Select((item, position) => new { Item = item, Position = position })
            .OrderBy(entry => indexSelector(entry.Item))
            .ThenBy(entry => entry.Position)
            .ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ordered)
        {
            T item = entry.Item;
            string originalName = nameSelector(item) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(originalName) || seen.Add(originalName))
            {
                continue;
            }

            for (int suffixNumber = 2; ; suffixNumber++)
            {
                string candidate = BuildSuffixedName(originalName, suffixNumber, maxBytes);
                if (!reserved.Add(candidate))
                {
                    continue;
                }

                nameSetter(item, candidate);
                changes.Add(new NameNormalizationChange(idSelector(item) ?? string.Empty, originalName, candidate));
                break;
            }
        }

        return changes;
    }

    public static string BuildSuffixedName(string originalName, int suffixNumber, int maxBytes)
    {
        string suffix = $"-{suffixNumber}";
        int bodyBudget = Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(suffix));
        return TrimToUtf8Budget(originalName, bodyBudget) + suffix;
    }

    public static string TrimToUtf8Budget(string value, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int usedBytes = 0;
        var builder = new StringBuilder(value.Length);
        foreach (Rune rune in value.EnumerateRunes())
        {
            string runeText = rune.ToString();
            int runeBytes = Encoding.UTF8.GetByteCount(runeText);
            if (usedBytes + runeBytes > maxBytes)
            {
                break;
            }

            builder.Append(runeText);
            usedBytes += runeBytes;
        }

        return builder.ToString();
    }
}
