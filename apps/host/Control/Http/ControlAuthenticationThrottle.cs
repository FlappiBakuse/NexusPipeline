using System.Net;

namespace NexusPipeline.Host.Control.Http;

internal sealed class ControlAuthenticationThrottle(TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, (long Since, int Failures)> _failures = [];
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    internal bool IsAllowed(IPAddress peer)
    {
        lock (_gate)
        {
            Prune();
            return _failures.TryGetValue(peer, out var value) ? value.Failures < 20 : _failures.Count < 256;
        }
    }
    internal void RecordFailure(IPAddress peer)
    {
        lock (_gate)
        {
            Prune();
            if (_failures.TryGetValue(peer, out var old))
                _failures[peer] = (old.Since, Math.Min(20, old.Failures + 1));
            else if (_failures.Count < 256) _failures.Add(peer, (clock.GetTimestamp(), 1));
        }
    }
    private void Prune()
    {
        foreach (var peer in _failures.Where(pair => clock.GetElapsedTime(pair.Value.Since) >= Window)
                     .Select(pair => pair.Key).ToArray()) _failures.Remove(peer);
    }
}
