namespace NexusPipeline.Platform.Storage;

public sealed class BlobReadScope : IDisposable
{
    [ThreadStatic] private static BlobReadScope? _current;
    internal const int MaximumReferences = 128;
    private readonly string _root;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly BlobReadScope? _previous = _current;
    private readonly Dictionary<string, (IDisposable Lease, LinkedListNode<string> Node)> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recent = new();
    private bool _disposed;

    private BlobReadScope(RuntimeInstance instance)
    {
        _root = PhysicalPathPolicy.NormalizeWithoutLinks(instance.RuntimeRoot);
        _current = this;
    }

    public static BlobReadScope Hold(RuntimeInstance instance)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new(instance);
    }

    internal static void Track(string path)
    {
        if (_current is not { } scope) return;
        var relative = Path.GetRelativePath(scope._root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("..\\", StringComparison.Ordinal)) return;
        if (scope._held.TryGetValue(path, out var current))
        {
            scope._recent.Remove(current.Node); scope._recent.AddLast(current.Node); return;
        }
        var lease = BlobReadStream.Retain(path);
        try
        {
            if (scope._held.Count == MaximumReferences)
            {
                var oldest = scope._recent.First!;
                var removed = scope._held[oldest.Value];
                scope._held.Remove(oldest.Value); scope._recent.RemoveFirst(); removed.Lease.Dispose();
            }
            scope._held.Add(path, (lease, scope._recent.AddLast(path)));
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
