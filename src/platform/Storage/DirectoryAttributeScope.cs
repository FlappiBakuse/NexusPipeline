namespace NexusPipeline.Platform.Storage;

public sealed class DirectoryAttributeScope : IDisposable
{
    [ThreadStatic] private static DirectoryAttributeScope? _current;
    private readonly DirectoryAttributeScope? _previous = _current;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly DirectoryAttributeProtection _protection;
    private readonly bool _ownsProtection;
    private bool _disposed;

    private DirectoryAttributeScope(DirectoryAttributeProtection protection, bool ownsProtection)
    {
        _protection = protection;
        _ownsProtection = ownsProtection;
        _current = this;
    }

    public static DirectoryAttributeScope Hold(RuntimeInstance instance) => new(DirectoryAttributeProtection.Acquire(instance), true);

    internal static DirectoryAttributeScope HoldDirectories(IEnumerable<string> directories) =>
        new(DirectoryAttributeProtection.AcquireDirectories(directories), true);

    internal static DirectoryAttributeScope Enter(DirectoryAttributeProtection protection) => new(protection, false);

    internal static bool TryReadCurrent(string path, out FileAttributes attributes)
    {
        if (_current is { } scope) return scope._protection.TryReadCurrent(path, out attributes);
        attributes = default;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        // A synchronous scope borrows protection without carrying thread-local state across an await.
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("directory_attributes_scope_order");
        _current = _previous;
        _disposed = true;
        if (_ownsProtection) _protection.Dispose();
    }
}
