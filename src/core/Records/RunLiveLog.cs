using System.Threading.Channels;

namespace NexusPipeline.Core.Records;

public sealed record LiveLogFrame(long Sequence, long ObservedOffset, string Source, int? FileSourceId,
    long? FileOffset, byte[] Bytes, bool GapBefore);
public sealed record LiveLogTail(RunLogKey Key, long LastSequence, long ObservedEnd, bool IsComplete,
    bool PrefixOmitted, IReadOnlyList<LiveLogFrame> Frames);

public interface ILiveLogReader
{
    LiveLogTail ReadTail(RunLogKey key, int maximumBytes = 65536);
    LiveLogSubscription Subscribe(RunLogKey key);
}

public sealed class LiveLogSubscription : IDisposable
{
    private readonly Channel<LiveLogFrame> _events = Channel.CreateBounded<LiveLogFrame>(
        new BoundedChannelOptions(8) { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Action<LiveLogSubscription> _remove;
    private readonly Action? _released;
    private int _ended;
    public LiveLogTail InitialTail { get; }
    public ChannelReader<LiveLogFrame> Events => _events.Reader;
    public string? FailureCode { get; private set; }

    internal LiveLogSubscription(LiveLogTail initial, Action<LiveLogSubscription> remove, Action? released)
    { InitialTail = initial; _remove = remove; _released = released; }

    internal bool Publish(LiveLogFrame frame) => Volatile.Read(ref _ended) == 0 && _events.Writer.TryWrite(frame);

    internal void End(string? reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        FailureCode = reason;
        _events.Writer.TryComplete(reason is null ? null : new IOException(reason));
        _released?.Invoke();
    }

    public void Dispose() { _remove(this); End("run_log_subscription_closed"); }
}

public sealed class LiveLogFeed(RunLogKey key)
{
    private readonly object _gate = new();
    private readonly Queue<LiveLogFrame> _tail = [];
    private readonly HashSet<LiveLogSubscription> _subscriptions = [];
    private int _tailBytes;
    private long _sequence, _observedEnd;
    private bool _complete, _closed, _omitted;

    public LiveLogTail ReadTail(int maximumBytes = 65536)
    {
        if (maximumBytes is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        lock (_gate) return Tail(maximumBytes);
    }

    public LiveLogSubscription Subscribe(Action? released = null)
    {
        lock (_gate)
        {
            if (_closed) throw new IOException("run_log_not_active");
            if (_subscriptions.Count == 8) throw new IOException("run_log_subscription_limit");
            var subscription = new LiveLogSubscription(Tail(65536), Remove, released);
            _subscriptions.Add(subscription); return subscription;
        }
    }

    public void Publish(long observedOffset, string source, ReadOnlySpan<byte> bytes,
        int? fileSourceId = null, long? fileOffset = null)
    {
        if (observedOffset < 0 || bytes.Length is < 1 or > 8192 || source is not ("stdout" or "stderr" or "file") ||
            (source == "file") != fileSourceId.HasValue || fileSourceId.HasValue != fileOffset.HasValue ||
            fileSourceId is < 0 or > 255 || fileOffset is < 0)
            throw new ArgumentException("run_log_live_frame_invalid");
        lock (_gate)
        {
            if (_closed) return;
            if (observedOffset < _observedEnd) throw new ArgumentException("run_log_live_offset_regressed");
            var frame = new LiveLogFrame(checked(++_sequence), observedOffset, source, fileSourceId, fileOffset,
                bytes.ToArray(), observedOffset != _observedEnd);
            _observedEnd = checked(observedOffset + bytes.Length);
            _tail.Enqueue(frame); _tailBytes += bytes.Length;
            while (_tailBytes > 65536 || _tail.Count > 32)
            { _tailBytes -= _tail.Dequeue().Bytes.Length; _omitted = true; }
            foreach (var subscription in _subscriptions.ToArray())
                if (!subscription.Publish(Copy(frame)))
                {
                    _subscriptions.Remove(subscription);
                    subscription.End("run_log_subscription_gap");
                }
        }
    }

    public void Complete(bool durableComplete, string? failureCode = null)
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true; _complete = durableComplete;
            foreach (var subscription in _subscriptions) subscription.End(failureCode);
            _subscriptions.Clear();
        }
    }

    private LiveLogTail Tail(int maximumBytes)
    {
        var frames = new List<LiveLogFrame>();
        var remaining = maximumBytes;
        foreach (var frame in _tail.Reverse())
        {
            if (remaining == 0) break;
            var size = Math.Min(remaining, frame.Bytes.Length);
            var skip = frame.Bytes.Length - size;
            frames.Add(frame with { ObservedOffset = frame.ObservedOffset + skip,
                FileOffset = frame.FileOffset + skip, Bytes = frame.Bytes.AsSpan(skip).ToArray(), GapBefore = frame.GapBefore || skip != 0 });
            remaining -= size;
        }
        frames.Reverse();
        return new(key, _sequence, _observedEnd, _complete,
            _omitted || frames.Sum(frame => frame.Bytes.Length) < _tailBytes, frames.AsReadOnly());
    }

    private void Remove(LiveLogSubscription subscription) { lock (_gate) _subscriptions.Remove(subscription); }
    private static LiveLogFrame Copy(LiveLogFrame frame) => frame with { Bytes = frame.Bytes.ToArray() };
}
