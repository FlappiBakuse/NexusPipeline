using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Core.Desktop;

public static class BrowserValueRules
{
    public static bool IsReady(BrowserLoginPolicy policy, IReadOnlyDictionary<string, SecretValue> values)
    {
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(values);
        var ids = policy.Cookies.Select(rule => rule.Id).Concat(policy.Storage.Select(rule => rule.Id))
            .Concat(policy.JsonRequests.Select(rule => rule.Id)).ToHashSet(StringComparer.Ordinal);
        if (values.Count > 28 || values.Keys.Any(id => !ids.Contains(id))) return false;
        var normalized = new Dictionary<string, char[]>(StringComparer.Ordinal);
        var total = 0;
        try
        {
            foreach (var (id, secret) in values)
            {
                if (secret is null) return false;
                var check = policy.Checks.SingleOrDefault(check => check.Id == id) ?? new(id, "nonEmpty", [], 32768);
                char[]? content = null;
                secret.Use(value => content = Extract(value, check));
                if (content is null) return false;
                normalized.Add(id, content);
                total = checked(total + content.Length); if (total > 65536) return false;
                if (!Valid(content, check)) return false;
            }
            foreach (var set in policy.EqualSets)
            {
                char[]? first = null;
                foreach (var id in set)
                    if (normalized.TryGetValue(id, out var value))
                    {
                        if (first is not null && !first.AsSpan().SequenceEqual(value)) return false;
                        first = value;
                    }
            }
            return policy.ReadySets.Any(set => set.All(normalized.ContainsKey));
        }
        finally { foreach (var content in normalized.Values) Array.Clear(content); }
    }

    private static char[]? Extract(ReadOnlySpan<char> value, LoginValueCheck check)
    {
        if (value.Length is < 1 or > 32768) return null;
        if (check.Path.IsEmpty) return value.ToArray();
        var bytes = new byte[Encoding.UTF8.GetByteCount(value)]; Encoding.UTF8.GetBytes(value, bytes);
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
            if (HasDuplicates(document.RootElement)) return null;
            var item = document.RootElement;
            foreach (var part in check.Path)
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(part, out item)) return null;
            return item.ValueKind == JsonValueKind.String ? item.GetString()!.ToCharArray() : null;
        }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool Valid(ReadOnlySpan<char> value, LoginValueCheck check)
    {
        if (value.Length < 1 || value.Length > check.MaximumLength) return false;
        if (check.Kind == "token") return IsToken(value);
        foreach (var character in value)
            if (check.Kind switch
            {
                "header" => character is <= ' ' or > '~',
                "cookie" => character is <= ' ' or > '~' or '"' or '\\' or ';' or ',',
                "digits" => character is < '0' or > '9',
                "nonEmpty" => false,
                _ => true
            }) return false;
        return true;
    }

    private static bool IsToken(ReadOnlySpan<char> value)
    {
        var end = value.Length;
        while (end > 0 && value[end - 1] == '=') end--;
        if (end == 0 || value.Length - end > 2) return false;
        foreach (var character in value[..end])
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '~' or '+' or '/' or '-'))
                return false;
        return true;
    }

    private static bool HasDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
                if (!names.Add(member.Name) || HasDuplicates(member.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasDuplicates(item)) return true;
        return false;
    }
}
