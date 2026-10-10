using System.Security.Cryptography;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Modules.ClientSessions;

internal sealed class ClientSessionService : IDisposable
{
    private sealed record Session(string Token, PluginClientSessionContext Context, string Authority, string? NativeOwner, CancellationTokenSource Stop);
    private readonly object _sync = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly string _hostId = Guid.NewGuid().ToString("N");
    private string _authority = "";
    private bool _disposed;
    private readonly Func<string>? _authorityProvider;
    internal ClientSessionService(Func<string>? authorityProvider = null) => _authorityProvider = authorityProvider;
    private void RefreshAuthority() { if (_authorityProvider is { } read) SetAuthority(read()); }

    internal void SetAuthority(string authority)
    {
        Session[] expired;
        lock (_sync)
        {
            if (_authority == authority) return;
            _authority = authority;
            expired = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var item in expired) item.Stop.Cancel();
    }

    internal (string Token, PluginClientSessionContext Context) CreateWeb()
    {
        RefreshAuthority();
        lock (_sync) return CreateLocked(null);
    }
    internal (string Token, PluginClientSessionContext Context) CreateDesktop(string verifiedOwner)
    {
        RefreshAuthority();
        if (string.IsNullOrWhiteSpace(verifiedOwner)) throw new ArgumentException("native_owner_required");
        lock (_sync)
        {
            var existing = _sessions.Values.FirstOrDefault(s => s.NativeOwner == verifiedOwner);
            if (existing is not null) return (existing.Token, existing.Context);
            return CreateLocked(verifiedOwner);
        }
    }

    private (string, PluginClientSessionContext) CreateLocked(string? nativeOwner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessions.Count >= 256) throw new InvalidOperationException("client_session_limit");
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var stop = new CancellationTokenSource();
        var context = new PluginClientSessionContext(_hostId, Guid.NewGuid().ToString("N"), nativeOwner is null ? "web" : "desktop", nativeOwner is not null) { Lifetime = stop.Token };
        _sessions.Add(token, new(token, context, _authority, nativeOwner, stop));
        return (token, context);
    }

    internal PluginClientSessionContext? Resolve(string? token, bool local)
    {
        RefreshAuthority();
        lock (_sync)
            return token is { Length: 64 } && _sessions.TryGetValue(token, out var session)
                && session.Authority == _authority && !session.Stop.IsCancellationRequested
                && (session.NativeOwner is null || local) ? session.Context : null;
    }

    internal void Revoke(string? token)
    {
        Session? session;
        lock (_sync) { if (token is null || !_sessions.Remove(token, out session)) return; }
        session.Stop.Cancel();
    }
    internal void RevokeDesktop(string owner)
    {
        Session[] expired;
        lock (_sync)
        {
            expired = _sessions.Values.Where(s => s.NativeOwner == owner).ToArray();
            foreach (var item in expired) _sessions.Remove(item.Token);
        }
        foreach (var item in expired) item.Stop.Cancel();
    }
    public void Dispose()
    {
        Session[] expired;
        lock (_sync) { if (_disposed) return; _disposed = true; expired = _sessions.Values.ToArray(); _sessions.Clear(); }
        foreach (var item in expired) item.Stop.Cancel();
    }
}
