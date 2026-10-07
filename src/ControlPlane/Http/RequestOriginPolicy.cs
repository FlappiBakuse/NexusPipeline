using System.Net;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.ControlPlane.Http;

internal sealed record OriginDecision(bool Allowed, string? CorsOrigin = null);

internal static class RequestOriginPolicy
{
    internal static OriginDecision Decide(WebRequest request, string method, string path, bool remoteBound, bool authenticated)
    {
        string? origin = request.Headers["Origin"];
        if (origin is null) return new(true);
        if (request.Headers.GetValues("Origin") is not { Length: 1 } || !TryOrigin(origin, out var source)
            || request.Headers["Host"] is not { } host || !TryOrigin("http://" + host, out var target)
            || request.Url?.Scheme != Uri.UriSchemeHttp || !string.Equals(request.Url.IdnHost, target!.IdnHost, StringComparison.OrdinalIgnoreCase)
            || request.Url.Port != target.Port) return new(false);
        bool localSource = source!.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(source.IdnHost.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
        bool lanSource = !localSource && NetInfo.ListLanAddresses().Any(address => string.Equals(address, source.IdnHost, StringComparison.OrdinalIgnoreCase));
        bool sameHost = string.Equals(source.IdnHost, target!.IdnHost, StringComparison.OrdinalIgnoreCase);
        // Loopback keeps the local address allowlist even when a domain resolves to this Host.
        if (sameHost && source.Port == target.Port)
            return new(localSource || lanSource || remoteBound && authenticated && RequestAccessPolicy.Connection(request) == PluginClientConnectionKind.Remote);
        bool statusProbe = (method == "GET" || method == "OPTIONS") && path.Equals("/api/status", StringComparison.OrdinalIgnoreCase);
        return statusProbe && sameHost && (localSource || lanSource) ? new(true, origin) : new(false);
    }

    private static bool TryOrigin(string value, out Uri? uri)
    {
        uri = null;
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.Length <= 7
            || value[7..].Any(ch => char.IsWhiteSpace(ch) || ch is '/' or '?' or '#' or '\\' or ',' or '@')
            || !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttp
            || uri.Port is < 1 or > 65535 || uri.UserInfo != "" || Uri.CheckHostName(uri.IdnHost.Trim('[', ']')) == UriHostNameType.Unknown)
            return false;
        return true;
    }
}
