using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class DatabaseValidationLease : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly StableDirectoryLease _directory;
    private readonly FileStream _file;
    private readonly PhysicalObjectIdentity _identity;
    private readonly SqliteConnection _connection;
    private readonly string _measurement;
    private long? _validated;
    private bool _disposed;

    internal DatabaseValidationLease(string path, string measurement = "installation.sql.validate")
    {
        _measurement = measurement;
        _path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        _directory = StableDirectoryLease.Acquire(Path.GetDirectoryName(_path)!);
        try
        {
            _file = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                _identity = PhysicalObjectIdentity.Read(_file.SafeFileHandle);
                if (PhysicalPathPolicy.NormalizeWithoutLinks(_path) != _path || PhysicalObjectIdentity.Read(_path) != _identity)
                    throw new IOException("database_validation_identity_changed");
                _connection = DatabaseConnections.Open(_path, readOnly: true, allowPooling: false);
            }
            catch { _file.Dispose(); throw; }
        }
        catch { _directory.Dispose(); throw; }
    }

    internal void RequireCurrent(Action<SqliteConnection, SqliteTransaction> validate)
    {
        lock (_gate)
        {
            RequireIdentity();
            var version = ReadVersion();
            if (_validated == version) return;
            _validated = null;
            using var timing = StartupProfile.Measure(_measurement);
            using (var transaction = _connection.BeginTransaction(deferred: true))
            {
                validate(_connection, transaction);
                transaction.Commit();
            }
            // data_version is comparable only on this continuously open, read-only connection.
            // A concurrent commit leaves the next read responsible for a fresh validation.
            if (ReadVersion() == version) _validated = version;
        }
    }

    internal T Read<T>(Func<SqliteConnection, SqliteTransaction, T> read)
    {
        lock (_gate)
        {
            RequireIdentity();
            using var transaction = _connection.BeginTransaction(deferred: true);
            var result = read(_connection, transaction);
            transaction.Commit();
            return result;
        }
    }

    internal void Invalidate() { lock (_gate) _validated = null; }

    private void RequireIdentity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (PhysicalObjectIdentity.Read(_file.SafeFileHandle) != _identity)
            throw new IOException("database_validation_identity_changed");
    }

    private long ReadVersion() => (long)DatabaseConnections.Scalar(_connection, "PRAGMA main.data_version;");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _connection.Dispose(); }
            finally { try { _file.Dispose(); } finally { _directory.Dispose(); } }
        }
    }
}
