using System.Net;

namespace NexusPipeline.Host.Control.Http;

internal static class RequestAuthority
{
    public static bool IsAllowed(HttpContext context)
    {
        var request = context.Request;
        if (context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer)) return false;
        // Matching two client headers cannot establish which local socket owns the authority.
        if (!Uri.TryCreate(request.Scheme + "://" + request.Host.Value + "/", UriKind.Absolute, out var authority)
            || authority.UserInfo.Length != 0 || authority.AbsolutePath != "/" || authority.Query.Length != 0
            || authority.Fragment.Length != 0 || authority.Port != context.Connection.LocalPort)
            return false;
        var local = context.Connection.LocalIpAddress;
        if (local is null || !IPAddress.IsLoopback(local)) return false;
        var localHost = authority.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        var literal = IPAddress.TryParse(authority.IdnHost.Trim('[', ']'), out var address) && address.Equals(local);
        if (!localHost && !literal) return false;
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var source) && source.UserInfo.Length == 0
            && source.AbsolutePath == "/" && source.Query.Length == 0 && source.Fragment.Length == 0
            && source.Scheme.Equals(authority.Scheme, StringComparison.OrdinalIgnoreCase)
            && source.IdnHost.Equals(authority.IdnHost, StringComparison.OrdinalIgnoreCase) && source.Port == authority.Port;
    }
}
