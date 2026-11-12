using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AttemptReadRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AttemptReadSession> _sessions = [];
    private bool _disposed;

    internal AttemptReadSession? Start(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid journal)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.Count >= 64) return null;
            var session = new AttemptReadSession(instance, database, contracts, journal, () => Remove(journal));
            try { _sessions.Add(journal, session); return session; }
            catch { session.Dispose(); throw; }
        }
    }

    internal AttemptReadSession? Find(Guid journal)
    { lock (_gate) return _sessions.GetValueOrDefault(journal); }

    internal IDisposable? HoldTerminal(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid journal)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sessions.ContainsKey(journal) ? null : Start(instance, database, contracts, journal);
        }
    }

    internal AttemptReadSession? Find(ExecutionPlan total)
    { lock (_gate) return _sessions.Values.FirstOrDefault(item => item.Matches(total)); }

    private void Remove(Guid journal) { lock (_gate) _sessions.Remove(journal); }

    public void Dispose()
    {
        AttemptReadSession[] sessions;
        lock (_gate) { _disposed = true; sessions = _sessions.Values.ToArray(); _sessions.Clear(); }
        foreach (var session in sessions) session.Dispose();
    }
}
