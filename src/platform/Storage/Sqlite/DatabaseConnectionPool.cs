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
    private readonly List<FileStream> _domains = [];
    internal sealed class Slot(int index)
    {
        internal int Index { get; } = index;
        internal bool Active;
        internal long Used;
        internal (DatabaseDomain Domains, bool ReadOnly)? Role;
        internal string? ConnectionString;
    }
    private readonly Slot[] _slots = [new(0), new(1), new(2), new(3)];
    private long _clock;
    private int _readers;
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
                foreach (var domain in DatabaseLayout.Domains.Where(domain => domain != DatabaseDomain.Runtime))
                {
                    var member = PhysicalPathPolicy.NormalizeWithoutLinks(DatabaseLayout.PathFor(_path, domain));
                    var input = new FileStream(member, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    _domains.Add(input);
                    if (PhysicalObjectIdentity.Read(input.SafeFileHandle) != PhysicalObjectIdentity.Read(member))
                        throw new IOException("database_read_identity_changed");
                }
                RequireIdentity();
                lock (PoolsGate)
                    if (!Pools.TryAdd(_path, this)) throw new IOException("database_read_owner_already_bound");
            }
            catch { foreach (var input in _domains) input.Dispose(); _file.Dispose(); throw; }
        }
        catch { _directory.Dispose(); throw; }
    }

    internal static BorrowLease? Borrow(string path, bool connection = true,
        DatabaseDomain domains = DatabaseDomain.All, bool readOnly = false)
    {
        lock (PoolsGate)
        {
            if (!Pools.TryGetValue(PhysicalPathPolicy.NormalizeSyntax(path), out var pool)) return null;
            lock (pool._gate)
            {
                pool.RequireIdentity();
                Slot? selected = null;
                if (connection)
                {
                    var role = (domains, readOnly);
                    foreach (var slot in pool._slots)
                    {
                        if (slot.Active) continue;
                        if (slot.Role == role) { selected = slot; break; }
                        if (selected is null || slot.Used < selected.Used) selected = slot;
                    }
                    if (selected is not null)
                    {
                        if (selected.Role != role && selected.ConnectionString is { } prior)
                        {
                            using var stale = new SqliteConnection(prior);
                            SqliteConnection.ClearPool(stale); selected.ConnectionString = null;
                        }
                        selected.Role = role; selected.Active = true; selected.Used = ++pool._clock;
                    }
                }
                pool._readers++;
                return new(pool, selected);
            }
        }
    }

    private void RequireIdentity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Retained handles deny deletion, so their file identities cannot change during this ownership.
        if (!_file.CanRead || _domains.Any(input => !input.CanRead))
            throw new IOException("database_read_identity_changed");
    }

    private void Track(SqliteConnection connection, Slot slot)
    {
        lock (_gate)
        {
            slot.ConnectionString = connection.ConnectionString;
            if (_disposed) SqliteConnection.ClearPool(connection);
        }
    }

    private void Return(Slot? slot)
    {
        lock (_gate)
        {
            _readers--;
            if (slot is not null) { slot.Active = false; slot.Used = ++_clock; }
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
            foreach (var slot in _slots)
                if (slot.ConnectionString is { } key)
                {
                    using var connection = new SqliteConnection(key);
                    SqliteConnection.ClearPool(connection); slot.ConnectionString = null;
                }
            if (_readers == 0) ReleaseFiles();
        }
    }

    private void ReleaseFiles()
    {
        try { foreach (var input in _domains) input.Dispose(); _file.Dispose(); }
        finally { _directory.Dispose(); }
    }

    internal sealed class BorrowLease : IDisposable
    {
        private DatabaseConnectionPool? _owner;
        private readonly Slot? _slot;
        internal BorrowLease(DatabaseConnectionPool owner, Slot? slot) { _owner = owner; _slot = slot; }
        private SqliteConnection? _connection;
        internal bool IsPooled => _slot is not null;
        internal string PoolSuffix => _slot is null ? "" : "&nexus_pool=" + _slot.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        internal string Path => _owner?._path ?? throw new ObjectDisposedException(nameof(BorrowLease));

        internal void Attach(SqliteConnection connection)
        {
            _connection = connection;
            connection.StateChange += OnStateChange;
            if (_slot is not null) _owner!.Track(connection, _slot);
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
            current.Return(_slot);
        }
    }
}
