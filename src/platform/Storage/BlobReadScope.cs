namespace NexusPipeline.Platform.Storage;

public sealed class BlobReadScope : IDisposable
{
    [ThreadStatic] private static BlobReadScope? _current;
    internal const int MaximumReferences = 128;
    private readonly string _root;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly BlobReadScope? _previous = _current;
    private readonly Action<string>? _capture;
    private readonly Dictionary<string, (IDisposable Lease, LinkedListNode<string> Node)> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recent = new();
    private bool _disposed;

    private BlobReadScope(RuntimeInstance instance, Action<string>? capture = null)
    {
        _root = PhysicalPathPolicy.NormalizeWithoutLinks(instance.RuntimeRoot);
        _capture = capture;
        _current = this;
    }

    public static BlobReadScope Hold(RuntimeInstance instance)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new(instance);
    }

    internal static BlobReadScope Capture(RuntimeInstance instance, Action<string> capture) => new(instance, capture);

    internal bool Retains(BlobReference reference) => !_disposed && _thread == Environment.CurrentManagedThreadId &&
        _held.ContainsKey(Path.GetFullPath(Path.Combine(_root, reference.RelativePath)));

    internal static void Track(string path)
    {
        if (_current is not { } scope) return;
        for (var observing = scope; observing is not null; observing = observing._previous)
            if (observing._capture is not null && path.StartsWith(observing._root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                observing._capture(path);
        var relative = Path.GetRelativePath(scope._root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("..\\", StringComparison.Ordinal)) return;
        scope.Retain(path);
        var outer = scope;
        for (var parent = scope._previous; parent is not null; parent = parent._previous)
            if (string.Equals(parent._root, scope._root, StringComparison.OrdinalIgnoreCase)) outer = parent;
        if (!ReferenceEquals(outer, scope)) outer.Retain(path);
    }

    private void Retain(string path)
    {
        if (_held.TryGetValue(path, out var current))
        {
            _recent.Remove(current.Node); _recent.AddLast(current.Node); return;
        }
        var lease = BlobReadStream.Retain(path);
        try
        {
            if (_held.Count == MaximumReferences)
            {
                var oldest = _recent.First!;
                var removed = _held[oldest.Value];
                _held.Remove(oldest.Value); _recent.RemoveFirst(); removed.Lease.Dispose();
            }
            _held.Add(path, (lease, _recent.AddLast(path)));
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // A synchronous verifier owns these byte-protection leases; they must never survive on another thread.
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("blob_read_scope_order");
        _current = _previous; _disposed = true;
        foreach (var item in _held.Values) item.Lease.Dispose();
        _held.Clear(); _recent.Clear();
    }
}
