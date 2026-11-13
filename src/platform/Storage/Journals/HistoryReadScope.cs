using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed class HistoryReadScope : IDisposable
{
    [ThreadStatic] private static HistoryReadScope? _current;
    private readonly HistoryReadScope? _previous = _current;
    private readonly StateDatabase _database;
    private readonly HistoryReadScope? _owner;
    private HistoryReadScope Owner => _owner ?? this;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly DatabaseReadScope _reads;
    private SqliteConnection? _sql;
    private long? _snapshotVersion;
    private readonly Dictionary<Guid, RecoveryRegistrySnapshot> _snapshots = [];
    private DirectoryAttributeScope? _paths;
    private BlobReadScope? _blobs;
    private InstanceReadScope? _identity;
    private bool _reading;
    private bool _disposed;

    internal HistoryReadScope(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        _database = database;
        if (_previous is { } prior && ReferenceEquals(prior._database, database)) _owner = prior.Owner;
        _reads = DatabaseReadScope.Hold(database.DatabasePath);
        try
        {
            _paths = DirectoryAttributeScope.Hold(instance);
            _blobs = BlobReadScope.Hold(instance);
            _identity = InstanceReadScope.Hold(instance, contracts);
            _current = this;
        }
        catch { Release(); throw; }
    }

    internal static T ReadDatabase<T>(StateDatabase database, Func<SqliteConnection, SqliteTransaction, T> read)
    {
        if (_current is { } current && ReferenceEquals(current._database, database) && !current.Owner._reading)
        {
            var scope = current.Owner;
            _ = DatabaseReadScope.ProtectedPath(database.DatabasePath);
            scope._sql ??= DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            scope._reading = true;
            try { return ReadInside(scope._sql, read); }
            finally { scope._reading = false; }
        }
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        return ReadInside(connection, read);
    }

    internal static RecoveryRegistrySnapshot ReadSnapshot(StateDatabase database, Guid? required,
        Func<SqliteConnection, SqliteTransaction, RecoveryRegistrySnapshot> read)
    {
        if (_current is not { } current || !ReferenceEquals(current._database, database) || current.Owner._reading)
            return ReadDatabase(database, read);
        var scope = current.Owner;
        _ = DatabaseReadScope.ProtectedPath(database.DatabasePath);
        scope._sql ??= DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var version = (long)DatabaseConnections.Scalar(scope._sql, "PRAGMA main.data_version;");
        if (scope._snapshotVersion != version) { scope._snapshots.Clear(); scope._snapshotVersion = version; }
        var key = required ?? Guid.Empty;
        if (scope._snapshots.TryGetValue(key, out var saved)) return saved;
        var value = ReadDatabase(database, read);
        if (scope._snapshots.Count < 32 && (long)DatabaseConnections.Scalar(scope._sql, "PRAGMA main.data_version;") == version)
            scope._snapshots.Add(key, value);
        return value;
    }

    private static T ReadInside<T>(SqliteConnection connection, Func<SqliteConnection, SqliteTransaction, T> read)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = read(connection, transaction);
        transaction.Commit();
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("history_read_scope_order");
        _disposed = true; _current = _previous;
        _snapshots.Clear(); _snapshotVersion = null;
        try { _sql?.Dispose(); }
        finally { Release(); }
    }

    private void Release()
    {
        try { _identity?.Dispose(); }
        finally
        {
            try { _blobs?.Dispose(); }
            finally { try { _paths?.Dispose(); } finally { _reads.Dispose(); } }
        }
    }
}
