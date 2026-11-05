using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Principal;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ControlAuthentication
{
    private static readonly ControlPermissionSnapshot LocalPermissions = new(false, false);
    private readonly object _requestKey = new();
    private readonly Guid _instanceId;
    private readonly bool _effectiveRemoteListener;
    private readonly int? _effectiveMcpPort;
    private readonly Func<HostSettingsSnapshot> _settings;
    private readonly ControlBearerVerifier _bearer;
    private readonly string _localSubject;
    private readonly ControlAuthenticationThrottle _throttle;

    internal ControlAuthentication(Guid instanceId, bool effectiveRemoteListener, int? effectiveMcpPort,
        Func<HostSettingsSnapshot> settings, ControlBearerVerifier bearer, TimeProvider clock)
    {
        if (instanceId == Guid.Empty || effectiveMcpPort is < 1 or > 65535)
            throw new ArgumentException("control_listener_identity_invalid");
        using var user = WindowsIdentity.GetCurrent();
        var sid = user.User?.Value ?? throw new IOException("control_windows_user_unavailable");
        _localSubject = "local:" + sid;
        _instanceId = instanceId; _effectiveRemoteListener = effectiveRemoteListener; _effectiveMcpPort = effectiveMcpPort;
        _settings = settings; _bearer = bearer; _throttle = new(clock);
    }

    internal async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null || !Authority(context))
        { await ControlErrors.Result(403, "access_denied").ExecuteAsync(context); return; }
        var local = IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer);
        var isMcp = context.Connection.LocalPort == _effectiveMcpPort;
        if (isMcp != (context.Request.Path.Value == "/control/v1/mcp"))
        { await ControlErrors.Result(404, "control_not_found").ExecuteAsync(context); return; }
        if (!isMcp && WebAssets.IsPublic(context)) { await next(context); return; }
        var current = isMcp || !local ? _settings().Values.Service : null;
        if (isMcp && (!local || !current!.McpEnabled))
        { await ControlErrors.Result(403, "access_denied").ExecuteAsync(context); return; }
        VerifiedControlBearer? credential = null;
        if (!local)
        {
            if (!_effectiveRemoteListener || !current!.AllowRemoteAccess) { await Deny(context, 403); return; }
            if (!_throttle.IsAllowed(peer)) { await Deny(context, 429); return; }
            var header = context.Request.Headers.Authorization;
            if (header.Count == 1 && header[0] is { Length: <= 280 } text &&
                AuthenticationHeaderValue.TryParse(text, out var parsed) &&
                parsed.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && parsed.Parameter is { } token)
            {
                try { credential = _bearer.Verify(token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                    System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or InvalidOperationException)
                { credential = null; }
            }
            if (credential is null)
            { _throttle.RecordFailure(peer); await Deny(context, 401); return; }
        }
        var origin = isMcp ? ControlOrigin.Mcp : local ? ControlOrigin.Local : ControlOrigin.Remote;
        Func<ControlPermissionSnapshot> permissions = origin == ControlOrigin.Local ? static () => LocalPermissions : () =>
            {
                var service = _settings().Values.Service;
                if (origin == ControlOrigin.Remote && !service.AllowRemoteAccess)
                    throw new UnauthorizedAccessException("control_remote_access_disabled");
                return new(service.McpEnabled && _effectiveMcpPort is not null, service.McpWriteEnabled);
            };
        using var credentialRequest = credential?.EnterRequest();
        context.Items[_requestKey] = new AuthenticatedControlRequest(origin,
            local ? _localSubject : "remote:" + _instanceId.ToString("D"), credential, permissions);
        try { await next(context); }
        finally { context.Items.Remove(_requestKey); }
    }

    internal AuthenticatedControlRequest Require(HttpContext context, ControlCapability capability)
    {
        var identity = context.Items[_requestKey] as AuthenticatedControlRequest ??
            throw new UnauthorizedAccessException("control_authenticated_origin_required");
        identity.Require(capability); return identity;
    }

    private static Task Deny(HttpContext context, int status) =>
        ControlErrors.Result(status, status == 429 ? "authentication_throttled" : "access_denied").ExecuteAsync(context);

    private static bool Authority(HttpContext context)
    {
        var request = context.Request;
        if (request.Headers.Host.Count != 1 || request.Headers.Origin.Count > 1 || request.Scheme is not ("http" or "https") ||
            !Uri.TryCreate(request.Scheme + "://" + request.Host.Value + "/", UriKind.Absolute, out var authority) ||
            authority.UserInfo.Length != 0 || authority.AbsolutePath != "/" || authority.Query.Length != 0 ||
            authority.Fragment.Length != 0 || authority.Port != context.Connection.LocalPort ||
            context.Connection.LocalIpAddress is not { } local) return false;
        var localName = IPAddress.IsLoopback(local) && authority.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        var literal = IPAddress.TryParse(authority.IdnHost.Trim('[', ']'), out var host) &&
            (host.IsIPv4MappedToIPv6 ? host.MapToIPv4() : host).Equals(local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local);
        var remoteDns = context.Connection.RemoteIpAddress is { } peer &&
            !IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer) &&
            authority.HostNameType == UriHostNameType.Dns &&
            !authority.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (!localName && !literal && !remoteDns) return false;
        if (request.Headers.Origin.Count == 0) return true;
        var value = request.Headers.Origin[0];
        return value is { Length: > 0 } && Uri.TryCreate(value, UriKind.Absolute, out var origin) &&
            origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0 &&
            origin.Scheme.Equals(authority.Scheme, StringComparison.OrdinalIgnoreCase) &&
            origin.IdnHost.Equals(authority.IdnHost, StringComparison.OrdinalIgnoreCase) && origin.Port == authority.Port;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class AuthenticatedControlRequest
{
    private readonly VerifiedControlBearer? _bearer;
    private readonly Func<ControlPermissionSnapshot> _current;
    internal ControlOrigin Origin { get; }
    internal string SubjectId { get; }
    internal AuthenticatedControlRequest(ControlOrigin origin, string subject, VerifiedControlBearer? bearer,
        Func<ControlPermissionSnapshot> current)
    { Origin = origin; SubjectId = subject; _bearer = bearer; _current = current; }
    internal void Require(ControlCapability capability)
    {
        _bearer?.RequireCurrent();
        if (!ControlCapabilityPolicy.IsAllowed(Origin, capability, _current()))
            throw new UnauthorizedAccessException("control_capability_denied");
    }
    internal IReadOnlyList<ControlCapabilityDescription> Visible()
    { _bearer?.RequireCurrent(); return ControlCapabilityPolicy.Visible(Origin, _current()); }

    internal void RequireClient(AuthenticatedControlRequest original)
    {
        if (Origin != original.Origin || SubjectId != original.SubjectId || (_bearer is null) != (original._bearer is null))
            throw new UnauthorizedAccessException("frontend_client_identity_changed");
        _bearer?.RequireSameCurrent(original._bearer!);
        if (!ControlCapabilityPolicy.IsAllowed(original.Origin, ControlCapability.ExtensionsRead, original._current()))
            throw new UnauthorizedAccessException("control_capability_denied");
        Require(ControlCapability.ExtensionsRead);
    }
}
