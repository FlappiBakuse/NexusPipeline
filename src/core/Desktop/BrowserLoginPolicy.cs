using NexusPipeline.Core.Secrets;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexusPipeline.Core.Desktop;

public sealed record LoginCookieRule(string Id, string Domain, string Path, string Name);
public sealed record LoginStorageRule(string Id, string Origin, string Storage, string Key);
public sealed record LoginJsonRule(string Id, string Origin, string Endpoint, string Method, ImmutableArray<string> Selector);
public sealed record LoginValueCheck(string Id, string Kind, ImmutableArray<string> Path, int MaximumLength);
public sealed record BrowserLoginPolicy(string Id, string StartUrl, ImmutableArray<string> Origins, ImmutableArray<string> PopupOrigins,
    ImmutableArray<LoginCookieRule> Cookies, ImmutableArray<LoginStorageRule> Storage, ImmutableArray<LoginJsonRule> JsonRequests,
    ImmutableArray<ImmutableArray<string>> ReadySets, ImmutableArray<LoginValueCheck> Checks, ImmutableArray<ImmutableArray<string>> EqualSets)
{
    public LoginPageActions PageActions { get; init; } = new([], []);
}

public sealed record LoginPageActions(ImmutableArray<LoginPageMatch> StopWhen, ImmutableArray<LoginPageClick> Clicks);
public sealed record LoginPageClick(LoginPageMatch Target, bool Unique, string RequiresSelector, string UnlessSelector);
public sealed record LoginPageMatch(string Selector, string Kind, ImmutableArray<string> Labels);

public static class BrowserPolicyRules
{
    public static BrowserLoginPolicy Freeze(BrowserLoginPolicy policy, IReadOnlyCollection<string> grantedOrigins)
    {
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(grantedOrigins); Id(policy.Id);
        if (policy.Origins.IsDefaultOrEmpty || policy.Origins.Length > 16 || policy.PopupOrigins.IsDefault || policy.PopupOrigins.Length > 8 ||
            policy.Cookies.IsDefault || policy.Cookies.Length > 16 || policy.Storage.IsDefault || policy.Storage.Length > 8 ||
            policy.JsonRequests.IsDefault || policy.JsonRequests.Length > 4 || policy.ReadySets.IsDefaultOrEmpty || policy.ReadySets.Length > 16 ||
            policy.Checks.IsDefault || policy.Checks.Length > 28 || policy.EqualSets.IsDefault || policy.EqualSets.Length > 8)
            throw Invalid();
        var origins = policy.Origins.Select(Origin).ToImmutableArray();
        if (origins.Distinct(StringComparer.Ordinal).Count() != origins.Length || origins.Any(value => !grantedOrigins.Contains(value, StringComparer.Ordinal)))
            throw new UnauthorizedAccessException("browser_login_origin_not_granted");
        var start = Address(policy.StartUrl);
        if (!origins.Contains(start.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal)) throw Invalid();
        var popups = policy.PopupOrigins.Select(Origin).ToImmutableArray();
        if (popups.Distinct(StringComparer.Ordinal).Count() != popups.Length || popups.Any(value => !origins.Contains(value, StringComparer.Ordinal))) throw Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cookie in policy.Cookies) RequireCookie(cookie, ids, origins);
        foreach (var storage in policy.Storage) RequireStorage(storage, ids, origins);
        foreach (var request in policy.JsonRequests) RequireJson(request, ids, origins, grantedOrigins);
        if (policy.Cookies.Select(rule => (rule.Domain, rule.Path, rule.Name)).Distinct().Count() != policy.Cookies.Length ||
            policy.Storage.Select(rule => (rule.Origin, rule.Storage, rule.Key)).Distinct().Count() != policy.Storage.Length) throw Invalid();
        if (ids.Count == 0) throw Invalid();
        foreach (var set in policy.ReadySets)
            if (set.IsDefaultOrEmpty || set.Length > 8 || set.Distinct(StringComparer.Ordinal).Count() != set.Length || set.Any(id => !ids.Contains(id))) throw Invalid();
        RequireChecks(policy, ids);
        RequirePageActions(policy.PageActions, start);
        if (JsonSerializer.SerializeToUtf8Bytes(policy).Length > 8192) throw Invalid();
        return policy with { Origins = origins, PopupOrigins = popups, Cookies = policy.Cookies.ToArray().ToImmutableArray(),
            Storage = policy.Storage.ToArray().ToImmutableArray(), JsonRequests = policy.JsonRequests.Select(rule => rule with {
                Selector = rule.Selector.ToArray().ToImmutableArray() }).ToImmutableArray(),
            ReadySets = policy.ReadySets.Select(set => set.ToArray().ToImmutableArray()).ToImmutableArray(),
            Checks = policy.Checks.Select(check => check with { Path = check.Path.ToArray().ToImmutableArray() }).ToImmutableArray(),
            EqualSets = policy.EqualSets.Select(set => set.ToArray().ToImmutableArray()).ToImmutableArray() };
    }

    private static void RequirePageActions(LoginPageActions actions, Uri start)
    {
        if (actions is null || actions.StopWhen.IsDefault || actions.StopWhen.Length > 6 ||
            actions.Clicks.IsDefault || actions.Clicks.Length > 2 ||
            actions.Clicks.IsEmpty && !actions.StopWhen.IsEmpty ||
            !actions.Clicks.IsEmpty && start.Scheme != "https") throw Invalid();
        foreach (var guard in actions.StopWhen) RequirePageMatch(guard);
        foreach (var click in actions.Clicks)
        {
            if (click is null || click.RequiresSelector is null || click.UnlessSelector is null) throw Invalid();
            if (click.RequiresSelector.Length > 0) Text(click.RequiresSelector, 384);
            if (click.UnlessSelector.Length > 0) Text(click.UnlessSelector, 384);
            RequirePageMatch(click.Target);
            if (click.Target.Kind is not ("visible" or "login-text" or "label")) throw Invalid();
        }
    }

    private static void RequirePageMatch(LoginPageMatch match)
    {
        if (match is null || match.Kind is not ("visible" or "login-text" or "other-text" or "account-link" or "absolute" or "label") ||
            match.Labels.IsDefault || match.Labels.Length > 16 || (match.Kind == "label" ? match.Labels.IsEmpty : !match.Labels.IsEmpty) ||
            match.Labels.Distinct(StringComparer.Ordinal).Count() != match.Labels.Length) throw Invalid();
        Text(match.Selector, 384);
        foreach (var label in match.Labels) Text(label, 48);
    }

    private static void RequireCookie(LoginCookieRule cookie, HashSet<string> ids, ImmutableArray<string> origins)
    {
        if (cookie is null) throw Invalid(); Id(cookie.Id); if (!ids.Add(cookie.Id)) throw Invalid();
        Text(cookie.Name, 128); Text(cookie.Path, 256);
        if (!cookie.Path.StartsWith('/') || cookie.Name.Any(c => c is <= ' ' or >= '\x7f' or ';' or '=' or ',')) throw Invalid();
        var domain = cookie.Domain;
        if (domain is not { Length: >= 1 and <= 253 } || domain != domain.ToLowerInvariant() ||
            domain.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))) throw Invalid();
        var host = domain.StartsWith('.') ? domain[1..] : domain;
        if (host.Length == 0 || host.StartsWith('.') || host.EndsWith('.') ||
            !origins.Any(value => new Uri(value).IdnHost == host || new Uri(value).IdnHost.EndsWith("." + host, StringComparison.Ordinal))) throw Invalid();
    }

    private static void RequireStorage(LoginStorageRule storage, HashSet<string> ids, ImmutableArray<string> origins)
    {
        if (storage is null) throw Invalid(); Id(storage.Id); if (!ids.Add(storage.Id)) throw Invalid();
        if (!origins.Contains(Origin(storage.Origin), StringComparer.Ordinal) || storage.Storage is not ("localStorage" or "sessionStorage")) throw Invalid();
        Text(storage.Key, 128);
    }

    private static void RequireJson(LoginJsonRule request, HashSet<string> ids, ImmutableArray<string> origins, IReadOnlyCollection<string> grantedOrigins)
    {
        if (request is null) throw Invalid(); Id(request.Id); if (!ids.Add(request.Id)) throw Invalid();
        if (!origins.Contains(Origin(request.Origin), StringComparer.Ordinal) || request.Method is not ("GET" or "POST") ||
            request.Selector.IsDefaultOrEmpty || request.Selector.Length > 8) throw Invalid();
        var endpoint = Address(request.Endpoint);
        if (endpoint.Query.Length != 0 || !grantedOrigins.Contains(endpoint.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal))
            throw new UnauthorizedAccessException("browser_login_origin_not_granted");
        foreach (var part in request.Selector) PathPart(part);
    }

    private static void RequireChecks(BrowserLoginPolicy policy, HashSet<string> ids)
    {
        var checkedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in policy.Checks)
        {
            if (check is null || !ids.Contains(check.Id) || !checkedIds.Add(check.Id) || check.Kind is not ("nonEmpty" or "header" or "token" or "cookie" or "digits") ||
                check.Path.IsDefault || check.Path.Length > 8 || check.MaximumLength is < 1 or > 32768) throw Invalid();
            if (!check.Path.IsEmpty && !policy.Storage.Any(rule => rule.Id == check.Id)) throw Invalid();
            foreach (var part in check.Path) PathPart(part);
        }
        foreach (var set in policy.EqualSets)
            if (set.IsDefault || set.Length is < 2 or > 16 || set.Distinct(StringComparer.Ordinal).Count() != set.Length || set.Any(id => !ids.Contains(id))) throw Invalid();
    }

    private static void PathPart(string value)
    { Text(value, 128); if (value is "__proto__" or "prototype" or "constructor") throw Invalid(); }

    public static string Origin(string value)
    {
        var uri = Address(value);
        if (uri.Query.Length != 0 || uri.AbsolutePath != "/" || uri.GetLeftPart(UriPartial.Authority) != value) throw Invalid();
        return value;
    }

    private static Uri Address(string value)
    {
        Text(value, 2048);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" && (uri.Scheme != "http" || !uri.IsLoopback) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || string.IsNullOrEmpty(uri.Host) || uri.Port < 1) throw Invalid();
        return uri;
    }

    private static void Id(string value)
    {
        Text(value, 80);
        if (value.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.'))) throw Invalid();
    }
    private static void Text(string value, int maximum)
    { if (value is null || value.Length is < 1 || value.Length > maximum || value.Any(char.IsControl)) throw Invalid(); }
    private static ArgumentException Invalid() => new("browser_login_policy_invalid");
}

public static class BrowserValueRules
{
    internal const int MaximumValueLength = 32768;
    internal const int MaximumTotalLength = 65536;
    internal const int MaximumValues = 28;
    internal const int MaximumJsonDepth = 16;

    public static bool IsReady(BrowserLoginPolicy policy, IReadOnlyDictionary<string, SecretValue> values)
    {
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(values);
        var ids = policy.Cookies.Select(rule => rule.Id).Concat(policy.Storage.Select(rule => rule.Id))
            .Concat(policy.JsonRequests.Select(rule => rule.Id)).ToHashSet(StringComparer.Ordinal);
        if (values.Count > MaximumValues || values.Keys.Any(id => !ids.Contains(id))) return false;
        var normalized = new Dictionary<string, char[]>(StringComparer.Ordinal);
        var total = 0;
        try
        {
            foreach (var (id, secret) in values)
            {
                if (secret is null) return false;
                var check = policy.Checks.SingleOrDefault(check => check.Id == id) ?? new(id, "nonEmpty", [], MaximumValueLength);
                char[]? content = null;
                secret.Use(value => content = Extract(value, check));
                if (content is null) return false;
                normalized.Add(id, content);
                total = checked(total + content.Length); if (total > MaximumTotalLength) return false;
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
        if (value.Length is < 1 or > MaximumValueLength) return null;
        if (check.Path.IsEmpty) return value.ToArray();
        var bytes = new byte[Encoding.UTF8.GetByteCount(value)]; Encoding.UTF8.GetBytes(value, bytes);
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = MaximumJsonDepth });
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
