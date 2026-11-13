using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class DatabaseConnections
{
    internal static SqliteConnection Open(string path, bool readOnly = false, bool allowPooling = true)
    {
        using var timing = StartupProfile.Measure("database.open");
        DatabaseConnectionPool.BorrowLease? shared = null;
        SqliteConnection? connection = null;
        try
        {
            var protectedPath = readOnly ? DatabaseReadScope.ProtectedPath(path) : null;
            if (allowPooling) shared = DatabaseConnectionPool.Borrow(path);
            connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = OperatingSystem.IsWindows() ? PhysicalPathPolicy.NativePath(shared?.Path ?? protectedPath ?? path) : path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = shared?.IsPooled ?? (allowPooling && protectedPath is not null),
                // Explicit foreign-key enforcement gives the instance pool a key unaffected by scoped pool cleanup.
                ForeignKeys = shared is null ? null : true,
                DefaultTimeout = 2
            }.ToString());
            connection.Open();
            if (shared is null && allowPooling && protectedPath is not null) DatabaseReadScope.Track(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF;";
            if (shared?.IsPooled == true) command.CommandText += " PRAGMA cache_size=-16384;";
            command.ExecuteNonQuery();
            if (!readOnly)
            {
                command.CommandText = "PRAGMA synchronous=FULL;";
                command.ExecuteNonQuery();
            }
            shared?.Attach(connection);
            return connection;
        }
        catch
        {
            try { connection?.Dispose(); }
            finally { shared?.Dispose(); }
            throw;
        }
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params object[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$" + index, values[index]);
        return command;
    }

    internal static object Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() ?? throw new IOException("Missing database metadata");
    }

    internal static void RejectLinks(string path)
    {
        PhysicalPathPolicy.NormalizeWithoutLinks(path);
    }
}
