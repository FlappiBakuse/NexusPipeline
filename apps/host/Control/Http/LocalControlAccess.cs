using System.Net;

namespace NexusPipeline.Host.Control.Http;

internal enum LocalControlCapability { SecretStatus, SecretRead, SecretWrite }

internal static class LocalControlAccess
{
    public static bool IsAllowed(HttpContext context, LocalControlCapability capability)
    {
        if (!Enum.IsDefined(capability)) return false;
        // Loopback operators share the Windows-user trust boundary; headers cannot grant this identity.
        return context.Connection.RemoteIpAddress is { } peer && IPAddress.IsLoopback(peer)
            && RequestAuthority.IsAllowed(context);
    }
}
