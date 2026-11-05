using System.Collections.Immutable;
using System.Text.Json;

namespace NexusPipeline.Core.Desktop;

public sealed record LoginCookieRule(string Id, string Domain, string Path, string Name);
public sealed record LoginStorageRule(string Id, string Origin, string Storage, string Key);
public sealed record LoginJsonRule(string Id, string Origin, string Endpoint, string Method, ImmutableArray<string> Selector);
public sealed record LoginValueCheck(string Id, string Kind, ImmutableArray<string> Path, int MaximumLength);
public sealed record BrowserLoginPolicy(string Id, string StartUrl, ImmutableArray<string> Origins, ImmutableArray<string> PopupOrigins,
    ImmutableArray<LoginCookieRule> Cookies, ImmutableArray<LoginStorageRule> Storage, ImmutableArray<LoginJsonRule> JsonRequests,
    ImmutableArray<ImmutableArray<string>> ReadySets, ImmutableArray<LoginValueCheck> Checks, ImmutableArray<ImmutableArray<string>> EqualSets);

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
        if (JsonSerializer.SerializeToUtf8Bytes(policy).Length > 8192) throw Invalid();
        return policy with { Origins = origins, PopupOrigins = popups, Cookies = policy.Cookies.ToArray().ToImmutableArray(),
            Storage = policy.Storage.ToArray().ToImmutableArray(), JsonRequests = policy.JsonRequests.Select(rule => rule with {
                Selector = rule.Selector.ToArray().ToImmutableArray() }).ToImmutableArray(),
            ReadySets = policy.ReadySets.Select(set => set.ToArray().ToImmutableArray()).ToImmutableArray(),
            Checks = policy.Checks.Select(check => check with { Path = check.Path.ToArray().ToImmutableArray() }).ToImmutableArray(),
            EqualSets = policy.EqualSets.Select(set => set.ToArray().ToImmutableArray()).ToImmutableArray() };
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
