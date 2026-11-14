using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class DatabaseValidationLease : IDisposable
{
    private readonly object _gate = new();
    private readonly StateDatabase _database;
    private readonly DatabaseDomain _domains;
    private readonly StableDirectoryLease _directory;
    private readonly FileStream _file;
    private readonly string _measurement;
    private SqliteConnection? _continuous;
    private DatabaseVersion? _validated;
    private bool _disposed;

    internal DatabaseValidationLease(StateDatabase database, string measurement = "installation.sql.validate",
        DatabaseDomain domains = DatabaseDomain.Extensions | DatabaseDomain.System)
    {
        _database = database; _measurement = measurement; _domains = domains;
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(database.DatabasePath);
        _directory = StableDirectoryLease.Acquire(Path.GetDirectoryName(path)!);
        try
        {
            _file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (PhysicalObjectIdentity.Read(_file.SafeFileHandle) != PhysicalObjectIdentity.Read(path))
            { _file.Dispose(); throw new IOException("database_validation_identity_changed"); }
        }
        catch { _directory.Dispose(); throw; }
    }

    private SqliteConnection? Continuous()
    {
        if (_database.RuntimeLease is not null) return null;
        return _continuous ??= DatabaseConnections.Open(_database.DatabasePath, readOnly: true, allowPooling: false, domains: _domains);
    }

    internal void RequireCurrent(Action<SqliteConnection, SqliteTransaction> validate)
    {
        lock (_gate)
        {
            RequireIdentity();
            var continuous = Continuous();
            var version = DatabaseVersion.Read(_database, continuous, _domains);
            if (_validated == version) return;
            _validated = null;
            using var timing = StartupProfile.Measure(_measurement);
            using var opened = continuous is null ? DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: _domains) : null;
            var connection = continuous ?? opened!;
            using (var transaction = connection.BeginTransaction(deferred: true))
            { validate(connection, transaction); transaction.Commit(); }
            if (DatabaseVersion.Read(_database, continuous, _domains) == version) _validated = version;
        }
    }

    internal T Read<T>(Func<SqliteConnection, SqliteTransaction, T> read)
    {
        lock (_gate)
        {
            RequireIdentity();
            var continuous = Continuous();
            using var opened = continuous is null ? DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: _domains) : null;
            var connection = continuous ?? opened!;
            using var transaction = connection.BeginTransaction(deferred: true);
            var value = read(connection, transaction); transaction.Commit(); return value;
        }
    }

    internal void Invalidate() { lock (_gate) _validated = null; }

    private void RequireIdentity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_file.CanRead) throw new IOException("database_validation_identity_changed");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _continuous?.Dispose(); }
            finally { _file.Dispose(); _directory.Dispose(); }
        }
    }
}
