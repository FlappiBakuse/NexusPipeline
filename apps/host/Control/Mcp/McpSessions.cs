using System.Diagnostics;

namespace NexusPipeline.Host.Control.Mcp;

internal sealed class McpSessions
{
    private const int MaximumSessions = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    internal string Initialize(string subject)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var expired in _sessions.Where(pair => Stopwatch.GetElapsedTime(pair.Value.LastUse, now) >= Lifetime)
                .Select(pair => pair.Key).ToArray()) _sessions.Remove(expired);
            if (_sessions.Count >= MaximumSessions) throw new McpProtocolException(-32000, "session_capacity");
            var id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            _sessions.Add(id, new(subject, now)); return id;
        }
    }

    internal void Require(string id, string subject, bool initialize = false)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.Subject != subject ||
                Stopwatch.GetElapsedTime(session.LastUse) >= Lifetime)
            { _sessions.Remove(id); throw new McpProtocolException(-32001, "session_expired"); }
            if (initialize) session.IsInitialized = true;
            else if (!session.IsInitialized) throw new McpProtocolException(-32002, "initialization_required");
            session.LastUse = Stopwatch.GetTimestamp();
        }
    }

    internal void End(string id, string subject)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.Subject != subject)
                throw new McpProtocolException(-32001, "session_expired");
            _sessions.Remove(id);
        }
    }

    private sealed class Session(string subject, long created)
    {
        internal string Subject { get; } = subject;
        internal long LastUse { get; set; } = created;
        internal bool IsInitialized { get; set; }
    }
}

internal sealed class McpProtocolException(int code, string reason) : Exception(reason)
{
    internal int Code { get; } = code;
}
