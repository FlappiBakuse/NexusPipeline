using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed class HistoryReadScope : IDisposable
{
    [ThreadStatic] private static HistoryReadScope? _current;
    private readonly HistoryReadScope? _previous = _current;
    private readonly StateDatabase _database;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly DatabaseReadScope _reads;
    private SqliteConnection? _sql;
    private DirectoryAttributeScope? _paths;
    private BlobReadScope? _blobs;
    private InstanceReadScope? _identity;
    private bool _reading;
    private bool _disposed;

    internal HistoryReadScope(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        _database = database;
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
        if (_current is { } scope && ReferenceEquals(scope._database, database) && !scope._reading)
        {
            _ = DatabaseReadScope.ProtectedPath(database.DatabasePath);
            scope._sql ??= DatabaseConnections.Open(database.DatabasePath, readOnly: true, allowPooling: false);
            scope._reading = true;
            try { return ReadInside(scope._sql, read); }
            finally { scope._reading = false; }
        }
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        return ReadInside(connection, read);
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
