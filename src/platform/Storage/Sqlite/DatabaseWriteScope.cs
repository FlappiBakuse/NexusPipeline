using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class DatabaseWriteScope : IDisposable
{
    [ThreadStatic] private static DatabaseWriteScope? _current;
    private readonly DatabaseWriteScope? _previous = _current;
    private readonly string _path;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private StableDirectoryLease? _directory;
    private FileStream? _file;
    private PhysicalObjectIdentity? _identity;
    private SqliteConnection? _connection;
    private bool _disposed;

    internal DatabaseWriteScope(string path)
    { _path = PhysicalPathPolicy.NormalizeSyntax(path); _current = this; }

    internal static SqliteConnection? Borrow(string path)
    {
        if (_current is not { } scope || !StringComparer.OrdinalIgnoreCase.Equals(scope._path, path)) return null;
        PhysicalPathPolicy.NormalizeWithoutLinks(path);
        if (scope._connection is null)
        {
            try
            {
                scope._directory = StableDirectoryLease.Acquire(Path.GetDirectoryName(path)!);
                scope._file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                scope._identity = PhysicalObjectIdentity.Read(scope._file.SafeFileHandle);
                if (PhysicalObjectIdentity.Read(path) != scope._identity) throw new IOException("database_write_identity_changed");
                scope._connection = DatabaseConnections.Open(path, domains: DatabaseDomain.All);
            }
            catch
            {
                scope._file?.Dispose(); scope._file = null;
                scope._directory?.Dispose(); scope._directory = null;
                throw;
            }
        }
        if (!scope._file!.CanRead) throw new IOException("database_write_identity_changed");
        return scope._connection;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("database_write_scope_order");
        _disposed = true; _current = _previous;
        try { _connection?.Dispose(); }
        finally { try { _file?.Dispose(); } finally { _directory?.Dispose(); } }
    }
}
