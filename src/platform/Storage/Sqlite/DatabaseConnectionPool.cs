using System.Data;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class DatabaseConnectionPool : IDisposable
{
    private static readonly object PoolsGate = new();
    private static readonly Dictionary<string, DatabaseConnectionPool> Pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly StableDirectoryLease _directory;
    private readonly FileStream _file;
    private readonly PhysicalObjectIdentity _identity;
    private readonly HashSet<string> _keys = [];
    private int _readers;
    private int _pooled;
    private bool _disposed;

    internal DatabaseConnectionPool(string path)
    {
        _path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        _directory = StableDirectoryLease.Acquire(Path.GetDirectoryName(_path)!);
        try
        {
            _file = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                _identity = PhysicalObjectIdentity.Read(_file.SafeFileHandle);
                if (PhysicalPathPolicy.NormalizeWithoutLinks(_path) != _path || PhysicalObjectIdentity.Read(_path) != _identity)
                    throw new IOException("database_read_identity_changed");
                RequireIdentity();
                lock (PoolsGate)
                    if (!Pools.TryAdd(_path, this)) throw new IOException("database_read_owner_already_bound");
            }
            catch { _file.Dispose(); throw; }
        }
        catch { _directory.Dispose(); throw; }
    }

    internal static BorrowLease? Borrow(string path, bool connection = true)
    {
        lock (PoolsGate)
        {
            if (!Pools.TryGetValue(PhysicalPathPolicy.NormalizeSyntax(path), out var pool)) return null;
            lock (pool._gate)
            {
                pool.RequireIdentity();
                var pooled = connection && pool._pooled < 4;
                pool._readers++;
                if (pooled) pool._pooled++;
                return new(pool, pooled);
            }
        }
    }

    private void RequireIdentity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The open file and every ancestor deny deletion for this owner's lifetime.
        if (PhysicalObjectIdentity.Read(_file.SafeFileHandle) != _identity)
            throw new IOException("database_read_identity_changed");
    }

    private void Track(SqliteConnection connection)
    {
        lock (_gate)
        {
            _keys.Add(connection.ConnectionString);
            if (_disposed) SqliteConnection.ClearPool(connection);
        }
    }

    private void Return(bool pooled)
    {
        lock (_gate)
        {
            _readers--;
            if (pooled) _pooled--;
            if (_disposed && _readers == 0) ReleaseFiles();
        }
    }

    public void Dispose()
    {
        lock (PoolsGate)
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Pools.Remove(_path);
            foreach (var key in _keys)
            {
                using var connection = new SqliteConnection(key);
                SqliteConnection.ClearPool(connection);
            }
            _keys.Clear();
            if (_readers == 0) ReleaseFiles();
        }
    }

    private void ReleaseFiles()
    {
        try { _file.Dispose(); }
        finally { _directory.Dispose(); }
    }

    internal sealed class BorrowLease(DatabaseConnectionPool owner, bool pooled) : IDisposable
    {
        private DatabaseConnectionPool? _owner = owner;
        private SqliteConnection? _connection;
        internal bool IsPooled => pooled;
        internal string Path => _owner?._path ?? throw new ObjectDisposedException(nameof(BorrowLease));

        internal void Attach(SqliteConnection connection)
        {
            _connection = connection;
            connection.StateChange += OnStateChange;
            if (pooled) _owner!.Track(connection);
        }

        private void OnStateChange(object? sender, StateChangeEventArgs change)
        {
            if (change.CurrentState == ConnectionState.Closed) Dispose();
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            if (_connection is { } connection) connection.StateChange -= OnStateChange;
            _connection = null;
            current.Return(pooled);
        }
    }
}
