using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NexusPipeline.Modules.Plugins.Contracts;

internal sealed record TaskDisplaySnapshot(string PluginId, string PluginVersion, string DefaultLocale,
    string LocalizationHash, Dictionary<string, Dictionary<string, string>> Messages)
{
    // Dictionary assets are bounded separately from the 1 MiB script output. History stores referenced keys only.
    internal const int ByteBudget = 256 * 1024;
    private static readonly Regex Key = new("^[A-Za-z0-9_.-]{1,160}$", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new("\\{([A-Za-z0-9_.-]+)\\}", RegexOptions.CultureInvariant);

    internal static void ValidateReference(JsonObject? reference, string version)
    {
        if (reference is null) return;
        TaskProtocolValidation.Require(version == "0.1.0", "unsupported task protocol text reference");
        string? kind = reference["kind"]?.GetValue<string>();
        if (kind == "literal")
        {
            TaskProtocolValidation.Require(reference.Count == 2 && reference["value"]?.GetValue<string>() is { Length: > 0 and <= 2048 }, "literal text");
            return;
        }
        TaskProtocolValidation.Require(kind == "plugin" && reference.Count == 4
            && reference.ContainsKey("key") && reference.ContainsKey("args") && reference.ContainsKey("fallback"), "plugin text fields");
        TaskProtocolValidation.Require(Key.IsMatch(reference["key"]?.GetValue<string>() ?? ""), "text key");
        TaskProtocolValidation.Require(reference["fallback"]?.GetValue<string>() is { Length: > 0 and <= 2048 }, "text fallback");
        TaskProtocolValidation.Require(reference["args"] is JsonObject { Count: <= 16 }, "text arguments");
        foreach (var (name, value) in (JsonObject)reference["args"]!)
        {
            TaskProtocolValidation.Require(Key.IsMatch(name), "text argument name");
            TaskProtocolValidation.Require(value is JsonValue scalar &&
                (scalar.TryGetValue<string>(out var text) && text.Length <= 256
                || scalar.TryGetValue<bool>(out _)
                || scalar.TryGetValue<double>(out var number) && double.IsFinite(number)), "text argument value");
        }
        TaskProtocolValidation.Require(Format(reference["fallback"]!.GetValue<string>(), (JsonObject)reference["args"]!) is not null, "text fallback placeholders");
    }

    internal TaskDisplaySnapshot Select(IEnumerable<JsonObject?> references)
    {
        var keys = references.Where(r => r?["kind"]?.GetValue<string>() == "plugin")
            .Select(r => r!["key"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var snapshot = this with { Messages = Messages.ToDictionary(p => p.Key,
            p => p.Value.Where(m => keys.Contains(m.Key)).ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal), StringComparer.Ordinal) };
        if (System.Text.Encoding.UTF8.GetByteCount(TaskProtocolJson.Write(snapshot)) > ByteBudget)
            return this with { Messages = new(StringComparer.Ordinal) }; // Retain tasks and safe TextRef fallbacks.
        return snapshot;
    }

    internal static string Resolve(JsonObject? reference, TaskDisplaySnapshot? snapshot, string locale, string fallback)
    {
        if (reference is null) return fallback;
        try
        {
            // Frozen historical TextRef syntax is identical to the first public
            // protocol; keep old reports readable without accepting old plugins.
            ValidateReference(reference, "0.1.0");
            if (reference["kind"]!.GetValue<string>() == "literal") return reference["value"]!.GetValue<string>();
            var args = (JsonObject)reference["args"]!;
            string key = reference["key"]!.GetValue<string>();
            if (snapshot is not null)
            {
                string normalized = locale.Replace('_', '-');
                var locales = snapshot.Messages.Keys.Order(StringComparer.Ordinal).ToArray();
                var candidates = locales.Where(l => l.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    .Concat(locales.Where(l => l.Split('-')[0].Equals(normalized.Split('-')[0], StringComparison.OrdinalIgnoreCase)))
                    .Append(snapshot.DefaultLocale).Distinct(StringComparer.Ordinal);
                foreach (string candidate in candidates)
                    if (snapshot.Messages.TryGetValue(candidate, out var messages) && messages.TryGetValue(key, out var message)
                        && Format(message, args) is { } resolved) return resolved;
            }
            return Format(reference["fallback"]!.GetValue<string>(), args) ?? fallback;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException) { return fallback; }
    }

    private static string? Format(string template, JsonObject args)
    {
        var matches = Placeholder.Matches(template);
        if (matches.Any(m => !args.ContainsKey(m.Groups[1].Value))) return null;
        // One substitution pass: argument content is literal, never another template or markup.
        string result = Placeholder.Replace(template, m => args[m.Groups[1].Value] is JsonValue value
            && value.TryGetValue<string>(out var text) ? text : args[m.Groups[1].Value]!.ToJsonString());
        return result.Length <= 4096 ? result : null;
    }
}
