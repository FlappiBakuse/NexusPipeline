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

    internal static T ReadDatabase<T>(StateDatabase database, Func<SqliteConnection, SqliteTransaction, T> read,
        DatabaseDomain domains = DatabaseDomain.Runtime)
    {
        if (_current is { } current && ReferenceEquals(current._database, database) && !current.Owner._reading)
        {
            var scope = current.Owner;
            _ = DatabaseReadScope.ProtectedPath(database.DatabasePath);
            RequireSql(scope, domains);
            scope._reading = true;
            try { return ReadInside(scope._sql!, read); }
            finally { scope._reading = false; }
        }
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: domains);
        return ReadInside(connection, read);
    }

    internal static RecoveryRegistrySnapshot ReadSnapshot(StateDatabase database, Guid? required,
        Func<SqliteConnection, SqliteTransaction, RecoveryRegistrySnapshot> read)
    {
        if (_current is not { } current || !ReferenceEquals(current._database, database) || current.Owner._reading)
            return ReadDatabase(database, read);
        var scope = current.Owner;
        _ = DatabaseReadScope.ProtectedPath(database.DatabasePath);
        RequireSql(scope, DatabaseDomain.Runtime);
        var version = ReadVersion(scope);
        if (scope._snapshotVersion != version) { scope._snapshots.Clear(); scope._snapshotVersion = version; }
        var key = required ?? Guid.Empty;
        if (scope._snapshots.TryGetValue(key, out var saved)) return saved;
        var value = ReadDatabase(database, read);
        if (scope._snapshots.Count < 32 && ReadVersion(scope) == version)
            scope._snapshots.Add(key, value);
        return value;
    }

    private static long ReadVersion(HistoryReadScope scope) => scope._database.RuntimeLease is not null
        ? scope._database.DomainSequence(DatabaseDomain.Runtime)
        : (long)DatabaseConnections.Scalar(scope._sql!, "PRAGMA main.data_version;");

    private static void RequireSql(HistoryReadScope scope, DatabaseDomain domains)
    {
        var current = scope._sql is null ? 0 : DatabaseConnections.Domains(scope._sql);
        if (scope._sql is not null && (current & domains) == domains) return;
        scope._sql?.Dispose();
        scope._snapshots.Clear(); scope._snapshotVersion = null;
        scope._sql = DatabaseConnections.Open(scope._database.DatabasePath, readOnly: true, domains: current | domains);
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
