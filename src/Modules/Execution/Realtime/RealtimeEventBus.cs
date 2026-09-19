using System.Text.Json;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Execution.Realtime;

/// <summary>
/// 宿主内的非阻塞实时事件扇出。每个连接拥有独立的有界队列，慢连接只影响自身。
/// </summary>
internal sealed class RealtimeEventBus
{
    private const int DefaultSubscriberCapacity = 256;

    private readonly object _sync = new();

    private readonly List<RealtimeEventSubscription> _subscribers = new();

    private readonly object _logSync = new();

    private readonly Dictionary<string, PendingLogBatch> _pendingLogs = new(StringComparer.Ordinal);

    private long _nextSequence;

    internal RealtimeEventEnvelope CreateEnvelope(string type, object? data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        JsonElement element = JsonSerializer.SerializeToElement(data ?? new { }, JsonOpts.Web);
        return new RealtimeEventEnvelope(
            SchemaVersion: 1,
            Sequence: Interlocked.Increment(ref _nextSequence),
            Timestamp: DateTimeOffset.UtcNow,
            Data: element);
    }

    internal RealtimeEventSubscription Subscribe(int capacity = DefaultSubscriberCapacity)
    {
        var subscription = new RealtimeEventSubscription(
            Math.Max(1, capacity),
            RemoveSubscription);
        lock (_sync)
        {
            _subscribers.Add(subscription);
        }
        return subscription;
    }

    internal void Publish(string type, object? data)
    {
        RealtimeEvent item = new(type, CreateEnvelope(type, data));
        RealtimeEventSubscription[] subscribers;
        lock (_sync)
        {
            subscribers = _subscribers.ToArray();
        }
        foreach (RealtimeEventSubscription subscriber in subscribers)
        {
            subscriber.TryEnqueue(item);
        }
    }

    internal void PublishLog(string runId, ExecutionLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }
        lock (_logSync)
        {
            if (!_pendingLogs.TryGetValue(runId, out PendingLogBatch? batch))
            {
                batch = new PendingLogBatch();
                batch.Timer = new System.Threading.Timer(
                    _ => FlushLog(runId),
                    state: null,
                    dueTime: TimeSpan.FromMilliseconds(200),
                    period: Timeout.InfiniteTimeSpan);
                _pendingLogs[runId] = batch;
            }
            batch.Entries.Add(entry);
        }
    }

    /// <summary>供单元测试和宿主收尾使用；正常运行由 200ms 定时器批量发送。</summary>
    internal void FlushPendingLogs()
    {
        string[] runIds;
        lock (_logSync)
        {
            runIds = _pendingLogs.Keys.ToArray();
        }
        foreach (string runId in runIds)
        {
            FlushLog(runId);
        }
    }

    /// <summary>在运行状态切换为已结束前发送该运行尚未到批次窗口的日志。</summary>
    internal void FlushPendingLogs(string runId)
    {
        if (!string.IsNullOrWhiteSpace(runId))
        {
            FlushLog(runId);
        }
    }

    private void FlushLog(string runId)
    {
        List<ExecutionLogEntry>? entries = null;
        lock (_logSync)
        {
            if (_pendingLogs.Remove(runId, out PendingLogBatch? batch))
            {
                batch.Timer?.Dispose();
                entries = batch.Entries.ToList();
            }
        }
        if (entries is { Count: > 0 })
        {
            Publish(RealtimeEventNames.RunLog, RealtimeEventProjection.RunLog(runId, entries));
        }
    }

    private void RemoveSubscription(RealtimeEventSubscription subscription)
    {
        lock (_sync)
        {
            _subscribers.Remove(subscription);
        }
    }

    private sealed class PendingLogBatch
    {
        internal readonly List<ExecutionLogEntry> Entries = new();

        internal System.Threading.Timer? Timer;
    }
}

internal readonly record struct RealtimeEventDelivery(bool Missed, RealtimeEvent? Event)
{
    internal static RealtimeEventDelivery MissedDelivery => new(true, null);

    internal static RealtimeEventDelivery EventDelivery(RealtimeEvent value) => new(false, value);
}

internal sealed class RealtimeEventSubscription : IDisposable
{
    private readonly object _sync = new();

    private readonly int _capacity;

    private readonly Action<RealtimeEventSubscription> _onDispose;

    private readonly Queue<RealtimeEvent> _queue = new();

    private TaskCompletionSource<bool> _signal = NewSignal();

    private bool _missed;

    private bool _disposed;

    internal RealtimeEventSubscription(
        int capacity,
        Action<RealtimeEventSubscription> onDispose)
    {
        _capacity = capacity;
        _onDispose = onDispose;
    }

    internal int Capacity => _capacity;

    internal bool TryEnqueue(RealtimeEvent item)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }
            if (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                _missed = true;
            }
            _queue.Enqueue(item);
            _signal.TrySetResult(true);
            return true;
        }
    }

    internal async ValueTask<RealtimeEventDelivery?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;
            lock (_sync)
            {
                if (_missed)
                {
                    _missed = false;
                    return RealtimeEventDelivery.MissedDelivery;
                }
                if (_queue.Count > 0)
                {
                    return RealtimeEventDelivery.EventDelivery(_queue.Dequeue());
                }
                if (_disposed)
                {
                    return null;
                }
                if (_signal.Task.IsCompleted)
                {
                    _signal = NewSignal();
                }
                signal = _signal.Task;
            }
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        bool notify;
        lock (_sync)
        {
            notify = !_disposed;
            _disposed = true;
            _queue.Clear();
            _signal.TrySetResult(true);
        }
        if (notify)
        {
            _onDispose(this);
        }
    }

    private static TaskCompletionSource<bool> NewSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
