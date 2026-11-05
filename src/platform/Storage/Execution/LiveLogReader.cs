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
    private readonly object _gate = new();
    private readonly Dictionary<RunLogKey, LiveLogWriter> _writers = [];
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
            if (_closed || _writers.Count >= 128 || _writers.ContainsKey(key)) throw new IOException("run_log_live_writer_conflict");
            var writer = new LiveLogWriter(new(key), () => Remove(key));
            _writers.Add(key, writer); return writer;
        }
    }

    internal LiveLogTail ReadTail(RunLogKey key, int maximumBytes)
    { lease.VerifyHeld(lease.Instance); return Find(key).Feed.ReadTail(maximumBytes); }

    internal LiveLogSubscription Subscribe(RunLogKey key)
    {
        lease.VerifyHeld(lease.Instance);
        var writer = Find(key);
        if (Interlocked.Increment(ref _subscribers) > 64)
        { Interlocked.Decrement(ref _subscribers); throw new IOException("run_log_subscription_limit"); }
        try { return writer.Feed.Subscribe(() => Interlocked.Decrement(ref _subscribers)); }
        catch { Interlocked.Decrement(ref _subscribers); throw; }
    }

    private LiveLogWriter Find(RunLogKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate) return !_closed && _writers.TryGetValue(key, out var writer) ? writer : throw new IOException("run_log_not_active");
    }

    private void Remove(RunLogKey key) { lock (_gate) _writers.Remove(key); }

    internal void Close()
    {
        LiveLogWriter[] active;
        lock (_gate) { _closed = true; active = _writers.Values.ToArray(); _writers.Clear(); }
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
