using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class DatabaseReadScope : IDisposable
{
    [ThreadStatic] private static DatabaseReadScope? _current;
    private readonly DatabaseReadScope? _previous = _current;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly string _path;
    private StableDirectoryLease? _directory;
    private FileStream? _file;
    private string? _pool;
    private bool _disposed;

    private DatabaseReadScope(string path)
    {
        _path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        _current = this;
    }

    private void Protect()
    {
        if (_file is not null) return;
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
        _ = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        scope.Protect();
        return scope._path;
    }

    internal static void Track(SqliteConnection connection)
    {
        if (_current is { } scope) scope._pool ??= connection.ConnectionString;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("database_read_scope_order");
        _disposed = true; _current = _previous;
        // Native connections must close while the file and its pathname are still protected.
        try
        {
            if (_pool is not null)
            {
                using var pool = new SqliteConnection(_pool);
                SqliteConnection.ClearPool(pool);
            }
        }
        finally { try { _file?.Dispose(); } finally { _directory?.Dispose(); } }
    }
}
