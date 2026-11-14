using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class ExecutionSqlScope(StateDatabase database) : IDisposable
{
    private StableDirectoryLease? _directory;
    private FileStream? _file;
    private SqliteConnection? _connection;
    private bool _disposed;

    internal T Read<T>(StateDatabase requestedDatabase, Func<SqliteConnection, T> read)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(database, requestedDatabase)) throw new IOException("execution_sql_scope_unbound");
        if (_connection is null)
        {
            try
            {
                _directory = StableDirectoryLease.Acquire(Path.GetDirectoryName(database.DatabasePath)!);
                // Pin the actual file without blocking other SQL writers or allowing a pathname replacement.
                _file = new(database.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _ = PhysicalObjectIdentity.Read(_file.SafeFileHandle);
                _connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
            }
            catch { Dispose(); throw; }
        }
        // The callback ends every command/reader. No transaction or SQL result survives this call.
        return read(_connection);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _connection?.Dispose(); }
        finally { try { _file?.Dispose(); } finally { _directory?.Dispose(); } }
    }
}
