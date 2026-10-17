namespace NexusPipeline.ControlPlane.Http;

internal sealed class RemoteAuthenticationThrottle
{
    private const int MaxAuthFailsBeforeLock = 5;
    private const int AuthLockSeconds = 60;
    private const int AuthFailIdlePruneSeconds = 600;
    private sealed class AuthFailState
    {
        internal int Fails;
        internal long LockedUntil;
        internal long LastActive;
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, AuthFailState> _failures = new();
    private long _lastPruneTicks;

    internal bool Authorize(string ip, Func<bool> tokenMatches, long now, out string? detail)
    {
        // Counting, successful reset and pruning must share one ordered decision boundary.
        lock (_sync)
        {
            Prune(now);
            if (_failures.TryGetValue(ip, out var state) && state.LockedUntil > now)
            {
                detail = $"失败次数过多，已锁定（剩余 {TimeSpan.FromTicks(state.LockedUntil - now).TotalSeconds:F0} 秒）";
                return false;
            }
            if (tokenMatches())
            {
                _failures.Remove(ip);
                detail = $"远程请求 {ip}，令牌匹配";
                return true;
            }
            if (!_failures.TryGetValue(ip, out var current)) _failures[ip] = current = new AuthFailState();
            current.LastActive = now;
            current.Fails++;
            if (current.Fails >= MaxAuthFailsBeforeLock)
            {
                current.LockedUntil = now + TimeSpan.FromSeconds(AuthLockSeconds).Ticks;
                current.Fails = 0;
                detail = $"远程请求 {ip}，令牌不匹配（已触发锁定 {AuthLockSeconds} 秒）";
            }
            else detail = $"远程请求 {ip}，令牌不匹配/缺失（失败 {current.Fails}/{MaxAuthFailsBeforeLock}）";
            return false;
        }
    }

    private void Prune(long now)
    {
        if (now - _lastPruneTicks < TimeSpan.FromSeconds(60).Ticks) return;
        _lastPruneTicks = now;
        foreach (var pair in _failures.ToArray())
            if (pair.Value.LockedUntil <= now && now - pair.Value.LastActive > TimeSpan.FromSeconds(AuthFailIdlePruneSeconds).Ticks)
                _failures.Remove(pair.Key);
    }
}
