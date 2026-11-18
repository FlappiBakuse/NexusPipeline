using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AttemptReadMemo(ExecutionPlan total) : IDisposable
{
    private static readonly object Gate = new();
    private static long _retainedBytes;
    private readonly Dictionary<(JsonElement, Type), object> _values = [];
    private readonly Dictionary<JsonElement, AttemptJournalView> _projections = [];
    private readonly Dictionary<JsonElement, IReadOnlyList<AttemptJournalView>> _views = [];
    private readonly HashSet<JsonElement> _roots = [];
    private long _bytes;
    private bool _disposed;

    internal bool TryRead<T>(JsonElement original, ExecutionPlan ownedTotal, out ImmutableArray<T> value)
    {
        lock (Gate)
        {
            value = default;
            if (_disposed || !ReferenceEquals(total, ownedTotal) || !_values.TryGetValue((original, typeof(T)), out var saved)) return false;
            value = (ImmutableArray<T>)saved; return true;
        }
    }

    internal void Remember<T>(JournalDocument original, ExecutionPlan ownedTotal, ImmutableArray<T> values)
    {
        lock (Gate)
        {
            if (_disposed || !ReferenceEquals(total, ownedTotal) || _values.Count >= 1024 || values.Length > 1024 ||
                _values.ContainsKey((original.Root, typeof(T))) || !Reserve(original, 64L + values.Length * 128L)) return;
            _values.Add((original.Root, typeof(T)), values);
        }
    }

    internal bool TryProjection(JsonElement original, ExecutionPlan ownedTotal, out AttemptJournalView value)
    {
        lock (Gate)
        {
            value = default;
            return !_disposed && ReferenceEquals(total, ownedTotal) && _projections.TryGetValue(original, out value);
        }
    }

    internal void RememberProjection(JournalDocument original, ExecutionPlan ownedTotal, AttemptJournalView value)
    {
        lock (Gate)
        {
            if (_disposed || !ReferenceEquals(total, ownedTotal) || _projections.Count >= 256 ||
                _projections.ContainsKey(original.Root) || !Reserve(original, 64L + value.RetainedSelectionBytes)) return;
            _projections.Add(original.Root, value);
        }
    }

    internal bool TryViews(JsonElement original, ExecutionPlan ownedTotal, out IReadOnlyList<AttemptJournalView> value)
    {
        lock (Gate)
        {
            value = null!;
            return !_disposed && ReferenceEquals(total, ownedTotal) && _views.TryGetValue(original, out value!);
        }
    }

    internal void RememberViews(JournalDocument original, ExecutionPlan ownedTotal, IReadOnlyList<AttemptJournalView> value)
    {
        lock (Gate)
        {
            if (_disposed || !ReferenceEquals(total, ownedTotal) || _views.Count >= 256 || value.Count > 32 ||
                _views.ContainsKey(original.Root) || !Reserve(original, 64L + value.Count * 64L)) return;
            _views.Add(original.Root, value);
        }
    }

    private bool Reserve(JournalDocument original, long bytes)
    {
        // All views of one root share its bytes and parse metadata.
        if (!_roots.Contains(original.Root)) bytes += original.TextLength * 8L;
        if (bytes > 67108864 - _retainedBytes) return false;
        _roots.Add(original.Root);
        _bytes += bytes; _retainedBytes += bytes;
        return true;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true; _values.Clear(); _roots.Clear(); _projections.Clear(); _views.Clear();
            _retainedBytes -= _bytes; _bytes = 0;
        }
    }
}
