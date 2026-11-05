using System.Globalization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;

namespace NexusPipeline.Host.Control.Http;

internal sealed class ControlQueryValues(IQueryCollection query, params string[] allowed)
{
    internal void Validate(int maximumValueLength = 256)
    {
        if (maximumValueLength is < 1 or > 1024) throw Invalid();
        if (query.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal) ||
            pair.Value.Count != 1 || !(pair.Value[0] is { Length: > 0 } value && value.Length <= maximumValueLength)))
            throw Invalid();
    }
    internal string? Text(string name) => query.TryGetValue(name, out var value) ? value[0] : null;
    internal int Integer(string name, int fallback)
    {
        if (Text(name) is not { } text) return fallback;
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
            value.ToString(CultureInfo.InvariantCulture) != text) throw Invalid();
        return value;
    }
    internal long LongInteger(string name, long fallback)
    {
        if (Text(name) is not { } text) return fallback;
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
            value.ToString(CultureInfo.InvariantCulture) != text) throw Invalid();
        return value;
    }
    internal Guid? OptionalId(string name) => Text(name) is { } text ? Id(text) : null;
    internal DateTimeOffset? Utc(string name)
    {
        if (Text(name) is not { } text) return null;
        if (!(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)) || text.Length is < 20 or > 40 || text != text.Trim() ||
            !DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                    "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value) ||
            value == default || value.Offset != TimeSpan.Zero) throw Invalid();
        return value;
    }
    internal string Digest(string name) =>
        Text(name) is { } value && FileTreePolicy.Digest(value) ? value : throw Invalid();
    internal static Guid Id(string text) =>
        Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && id.ToString("D") == text ? id : throw Invalid();
    internal static RunLogRole Role(string role) => role switch
        { "main" => RunLogRole.Main, "preHook" => RunLogRole.PreHook, "postHook" => RunLogRole.PostHook, _ => throw Invalid() };
    internal BusinessOutcome? Business(string name) => Text(name) switch
    {
        null => null, "unverified" => BusinessOutcome.Unverified, "succeeded" => BusinessOutcome.Succeeded,
        "partial" => BusinessOutcome.Partial, "failed" => BusinessOutcome.Failed,
        "notApplicable" => BusinessOutcome.NotApplicable, "cancelled" => BusinessOutcome.Cancelled, _ => throw Invalid()
    };
    internal LifecycleOutcome? Lifecycle(string name) => Text(name) switch
    {
        null => null, "completed" => LifecycleOutcome.Completed, "cancelled" => LifecycleOutcome.Cancelled,
        "interrupted" => LifecycleOutcome.Interrupted, "blocked" => LifecycleOutcome.Blocked,
        "skipped" => LifecycleOutcome.Skipped, _ => throw Invalid()
    };
    private static ControlRequestException Invalid() => new(400, "query_invalid");
}
