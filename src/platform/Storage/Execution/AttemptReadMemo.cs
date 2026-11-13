using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AttemptReadMemo(ExecutionPlan total) : IDisposable
{
    private static readonly object Gate = new();
    private static long _retainedBytes;
    private readonly Dictionary<(JsonElement, Type), object> _values = [];
    private readonly Dictionary<JsonElement, JsonElement> _projections = [];
    private readonly Dictionary<JsonElement, IReadOnlyList<JsonElement>> _views = [];
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

    internal void Remember<T>(JsonElement original, ExecutionPlan ownedTotal, ImmutableArray<T> values)
    {
        lock (Gate)
        {
            if (_disposed || !ReferenceEquals(total, ownedTotal) || _values.Count >= 1024 || values.Length > 1024 ||
                _values.ContainsKey((original, typeof(T)))) return;
            var bytes = 64L + values.Length * 128L + (_roots.Contains(original) ? 0 : original.GetRawText().Length * 8L);
            if (bytes > 67108864 - _retainedBytes) return;
            _values.Add((original, typeof(T)), values); _roots.Add(original);
            _bytes += bytes; _retainedBytes += bytes;
        }
    }

    internal bool TryProjection(JsonElement original, ExecutionPlan ownedTotal, out JsonElement value)
    {
        lock (Gate)
        {
            value = default;
            return !_disposed && ReferenceEquals(total, ownedTotal) && _projections.TryGetValue(original, out value);
        }
    }

    internal void RememberProjection(JsonElement original, ExecutionPlan ownedTotal, JsonElement value)
    {
        lock (Gate)
        {
            if (_disposed || !ReferenceEquals(total, ownedTotal) || _projections.Count >= 256 || _projections.ContainsKey(original)) return;
            var bytes = 64L + (original.Equals(value) ? 0 : value.GetRawText().Length * 4L);
            if (bytes > 67108864 - _retainedBytes) return;
            _projections.Add(original, value); _bytes += bytes; _retainedBytes += bytes;
        }
    }

    internal bool TryViews(JsonElement original, ExecutionPlan ownedTotal, out IReadOnlyList<JsonElement> value)
    {
        lock (Gate)
        {
            value = null!;
            return !_disposed && ReferenceEquals(total, ownedTotal) && _views.TryGetValue(original, out value!);
        }
    }

    internal void RememberViews(JsonElement original, ExecutionPlan ownedTotal, IReadOnlyList<JsonElement> value)
    {
        lock (Gate)
        {
            if (_disposed || !ReferenceEquals(total, ownedTotal) || _views.Count >= 256 || value.Count > 32 || _views.ContainsKey(original)) return;
            var bytes = 64L + value.Count * 32L + value.Where(item => !item.Equals(original)).Sum(item => item.GetRawText().Length * 4L);
            if (bytes > 67108864 - _retainedBytes) return;
            _views.Add(original, Array.AsReadOnly(value.ToArray())); _bytes += bytes; _retainedBytes += bytes;
        }
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
