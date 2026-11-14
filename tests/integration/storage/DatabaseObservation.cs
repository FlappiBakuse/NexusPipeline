using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal sealed class DatabaseObservation : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    internal DatabaseObservation(StateDatabase database)
    {
        _connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        try
        {
            _transaction = _connection.BeginTransaction(deferred: true);
            using var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = "SELECT count(*) FROM main.operation_journals;";
            _ = command.ExecuteScalar();
        }
        catch { _connection.Dispose(); throw; }
    }

    public void Dispose() { _transaction.Dispose(); _connection.Dispose(); }
}
