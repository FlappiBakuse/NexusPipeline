using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Control;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class FrontendClients(TimeProvider clock) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private const string Header = "X-Nexus-Frontend-Client";
    private bool _stopped;

    internal void Map(WebApplication app, ControlAuthentication authority)
    {
        app.Lifetime.ApplicationStopped.Register(Dispose);
        app.MapPost("/control/v1/extensions/clients", (HttpContext context) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead);
            Empty(context);
            return Results.Json(Create(caller, Origin(context)));
        });
        app.MapPost("/control/v1/extensions/clients/retire", (HttpContext context) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead);
            Empty(context);
            Retire(RequiredToken(context), caller, Origin(context));
            return Results.NoContent();
        });
    }

    internal ClientIdentity? Read(HttpContext context, AuthenticatedControlRequest caller)
    {
        if (!context.Request.Headers.ContainsKey(Header)) return null;
        return Resolve(RequiredToken(context), caller, Origin(context));
    }

    internal ClientReceipt Create(AuthenticatedControlRequest caller, string origin)
    {
        caller.Require(ControlCapability.ExtensionsRead);
        var expired = new List<Grant>(); ClientReceipt result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            var now = clock.GetUtcNow();
            foreach (var key in _grants.Where(item => item.Value.ExpiresAt <= now || item.Value.Lifetime.IsCancellationRequested).Select(item => item.Key).ToArray())
            { _grants[key].Retired = true; expired.Add(_grants[key]); _grants.Remove(key); }
            if (_grants.Count >= 128) throw new ControlRequestException(429, "frontend_clients_full");
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var grant = new Grant(Guid.NewGuid(), caller, origin, now.AddHours(24), clock);
            _grants.Add(Digest(token), grant);
            result = new(grant.ClientId, token, grant.ExpiresAt);
        }
        foreach (var grant in expired) grant.End();
        return result;
    }

    internal ClientIdentity Resolve(string token, AuthenticatedControlRequest caller, string origin)
    {
        caller.Require(ControlCapability.ExtensionsRead);
        Grant grant;
        lock (_gate)
        {
            if (_stopped || !ValidToken(token) || !_grants.TryGetValue(Digest(token), out var found) ||
                found.SubjectId != caller.SubjectId || found.CallerOrigin != caller.Origin || found.Origin != origin)
                throw new UnauthorizedAccessException("frontend_client_invalid");
            grant = found;
        }
        Require(grant, caller);
        return new(grant.ClientId, () =>
        {
            try { Require(grant, caller); }
            catch { grant.End(); throw; }
        }, grant.Lifetime);
    }

    internal void Retire(string token, AuthenticatedControlRequest caller, string origin)
    {
        var identity = Resolve(token, caller, origin);
        Grant? retired = null;
        lock (_gate)
        {
            var key = Digest(token);
            if (_grants.TryGetValue(key, out var grant) && grant.ClientId == identity.ClientId)
            { grant.Retired = true; retired = grant; _grants.Remove(key); }
        }
        retired?.End();
    }

    private void Require(Grant grant, AuthenticatedControlRequest caller)
    {
        bool expired;
        lock (_gate) expired = _stopped || grant.Retired || grant.ExpiresAt <= clock.GetUtcNow() || grant.Lifetime.IsCancellationRequested;
        if (expired)
        {
            grant.End(); throw new UnauthorizedAccessException("frontend_client_expired");
        }
        try { caller.RequireClient(grant.Caller); }
        catch { grant.End(); throw; }
    }

    public void Dispose()
    {
        Grant[] grants;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true; grants = _grants.Values.ToArray(); _grants.Clear();
        }
        foreach (var grant in grants) grant.End();
    }

    private static string Origin(HttpContext context) => context.Request.Scheme + "://" +
        (context.Request.Host.Value?.ToLowerInvariant() ?? throw new UnauthorizedAccessException("frontend_client_origin_missing"));
    private static bool ValidToken(string token) => token.Length == 64 && token.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Digest(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    private static string RequiredToken(HttpContext context)
    {
        var values = context.Request.Headers[Header];
        if (values.Count != 1 || values[0] is not { } token || !ValidToken(token))
            throw new UnauthorizedAccessException("frontend_client_invalid");
        return token;
    }

    private static void Empty(HttpContext context)
    {
        if (context.Request.QueryString.HasValue || context.Request.ContentLength is not (null or 0) ||
            context.Request.Headers.TransferEncoding.Count != 0)
            throw new ControlRequestException(400, "request_shape_invalid");
    }

    private sealed class Grant
    {
        private readonly CancellationTokenSource _owner;
        private int _ended;
        internal Guid ClientId { get; }
        internal AuthenticatedControlRequest Caller { get; }
        internal string SubjectId => Caller.SubjectId;
        internal ControlOrigin CallerOrigin => Caller.Origin;
        internal string Origin { get; }
        internal DateTimeOffset ExpiresAt { get; }
        internal bool Retired { get; set; }
        internal CancellationToken Lifetime { get; }
        internal Grant(Guid clientId, AuthenticatedControlRequest caller, string origin, DateTimeOffset expiresAt, TimeProvider clock)
        {
            ClientId = clientId; Caller = caller; Origin = origin; ExpiresAt = expiresAt;
            _owner = new(TimeSpan.FromHours(24), clock); Lifetime = _owner.Token;
        }
        internal void End()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            try { _owner.Cancel(); }
            catch (AggregateException) { }
            finally { _owner.Dispose(); }
        }
    }

    internal sealed record ClientReceipt(Guid ClientId, string Token, DateTimeOffset ExpiresAt);
    internal sealed record ClientIdentity(Guid ClientId, Action RequireCurrent, CancellationToken Lifetime);
}
