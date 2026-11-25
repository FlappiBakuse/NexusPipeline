using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class LiveLogReader : ILiveLogReader
{
    private readonly InstanceLease _lease;
    public LiveLogReader(RuntimeInstance instance, InstanceLease lease)
    { lease.VerifyHeld(instance); _lease = lease; }
    public LiveLogTail ReadTail(RunLogKey key, int maximumBytes = 65536) => _lease.LiveLogs.ReadTail(key, maximumBytes);
    public LiveLogSubscription Subscribe(RunLogKey key) => _lease.LiveLogs.Subscribe(key);
}

internal sealed class LiveLogHub(InstanceLease lease)
{
    private const int MaximumWriters = 128;
    private readonly object _gate = new();
    private readonly Dictionary<RunLogKey, LiveLogWriter> _writers = [];
    private readonly OrderedDictionary<RunLogKey, LiveLogFeed> _finished = [];
    private int _subscribers;
    private bool _closed;

    internal LiveLogWriter Register(RuntimeInstance instance, StateDatabase database,
        Serialization.ContractReader contracts, StepEvidenceBinding binding, string role = "process")
    {
        lease.VerifyHeld(instance);
        _ = new StepEvidenceReader(instance, database, contracts).Context(binding, role);
        var logRole = role switch { "process" => RunLogRole.Main, "preHook" => RunLogRole.PreHook,
            "postHook" => RunLogRole.PostHook, _ => throw new IOException("run_log_live_role_invalid") };
        var key = new RunLogKey(binding.RunId, binding.AttemptId, binding.StepId, logRole);
        lock (_gate)
        {
            if (_closed || _writers.Count >= MaximumWriters || _writers.ContainsKey(key))
                throw new IOException("run_log_live_writer_conflict");
            _finished.Remove(key);
            var writer = new LiveLogWriter(new(key), () => Finish(key));
            _writers.Add(key, writer); return writer;
        }
    }

    internal LiveLogTail ReadTail(RunLogKey key, int maximumBytes)
    { lease.VerifyHeld(lease.Instance); return Find(key).ReadTail(maximumBytes); }

    internal LiveLogSubscription Subscribe(RunLogKey key)
    {
        lease.VerifyHeld(lease.Instance);
        var feed = Find(key);
        if (Interlocked.Increment(ref _subscribers) > 64)
        { Interlocked.Decrement(ref _subscribers); throw new IOException("run_log_subscription_limit"); }
        try { return feed.Subscribe(() => Interlocked.Decrement(ref _subscribers)); }
        catch { Interlocked.Decrement(ref _subscribers); throw; }
    }

    private LiveLogFeed Find(RunLogKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_writers.TryGetValue(key, out var writer)) return writer.Feed;
            if (_finished.TryGetValue(key, out var feed)) return feed;
            throw new IOException("run_log_not_active");
        }
    }

    private void Finish(RunLogKey key)
    {
        lock (_gate)
        {
            if (!_writers.Remove(key, out var writer)) return;
            if (_finished.Count == MaximumWriters * 2) _finished.RemoveAt(0);
            _finished.Add(key, writer.Feed);
        }
    }

    internal void Release(Guid runId, Guid attemptId)
    {
        lock (_gate)
            foreach (var key in _finished.Keys.Where(key => key.RunId == runId && key.AttemptId == attemptId).ToArray())
                _finished.Remove(key);
    }

    internal void Close()
    {
        LiveLogWriter[] active;
        lock (_gate) { _closed = true; active = _writers.Values.ToArray(); _writers.Clear(); _finished.Clear(); }
        foreach (var writer in active) writer.Dispose();
    }
}

internal sealed class LiveLogWriter(LiveLogFeed feed, Action release) : IDisposable
{
    internal LiveLogFeed Feed { get; } = feed;
    private int _disposed;
    internal void Write(long offset, Processes.ProcessOutputSource source, ReadOnlySpan<byte> bytes, int? fileId, long? fileOffset) =>
        Feed.Publish(offset, fileId.HasValue ? "file" : source == Processes.ProcessOutputSource.StandardOutput ? "stdout" : "stderr",
            bytes, fileId, fileOffset);
    internal void Complete(bool durableComplete, string? reason = null) => Feed.Complete(durableComplete, reason);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Feed.Complete(false, "run_log_writer_stopped"); release();
    }
}
