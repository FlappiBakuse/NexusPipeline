namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class DatabaseReadScope : IDisposable
{
    [ThreadStatic] private static DatabaseReadScope? _current;
    private readonly DatabaseReadScope? _previous = _current;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly string _path;
    private readonly DatabaseReadScope? _owner;
    private readonly DatabaseConnectionPool.BorrowLease? _shared;
    private DatabaseReadScope Owner => _owner ?? this;
    private StableDirectoryLease? _directory;
    private FileStream? _file;
    private bool _disposed;

    private DatabaseReadScope(string path)
    {
        _path = PhysicalPathPolicy.NormalizeSyntax(path);
        if (_previous is { } prior && StringComparer.OrdinalIgnoreCase.Equals(prior._path, _path)) _owner = prior.Owner;
        else
        {
            _shared = DatabaseConnectionPool.Borrow(_path, connection: false);
            _path = _shared?.Path ?? PhysicalPathPolicy.NormalizeWithoutLinks(_path);
        }
        _current = this;
    }

    private void Protect()
    {
        if (_shared is not null || _file is not null) return;
        _ = PhysicalPathPolicy.NormalizeWithoutLinks(_path);
        _directory = StableDirectoryLease.Acquire(Path.GetDirectoryName(_path)!);
        try
        {
            _file = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try { _ = PhysicalObjectIdentity.Read(_file.SafeFileHandle); }
            catch { _file.Dispose(); throw; }
        }
        catch { _directory.Dispose(); throw; }
    }

    internal static DatabaseReadScope Hold(string path) => new(path);

    internal static string? ProtectedPath(string path)
    {
        if (_current is not { } scope || !StringComparer.OrdinalIgnoreCase.Equals(
                PhysicalPathPolicy.NormalizeSyntax(path), scope._path)) return null;
        scope.Owner.Protect();
        return scope._path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("database_read_scope_order");
        _disposed = true; _current = _previous;
        if (_owner is not null) return;
        try { _shared?.Dispose(); }
        finally { try { _file?.Dispose(); } finally { _directory?.Dispose(); } }
    }
}
