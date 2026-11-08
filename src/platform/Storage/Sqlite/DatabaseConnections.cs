using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class DatabaseConnections
{
    internal static SqliteConnection Open(string path, bool readOnly = false, bool allowPooling = true)
    {
        using var timing = StartupProfile.Measure("database.open");
        var protectedPath = readOnly ? DatabaseReadScope.ProtectedPath(path) : null;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = OperatingSystem.IsWindows() ? PhysicalPathPolicy.NativePath(protectedPath ?? path) : path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = allowPooling && protectedPath is not null,
            DefaultTimeout = 2
        }.ToString());
        try
        {
            connection.Open();
            if (allowPooling && protectedPath is not null) DatabaseReadScope.Track(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF;";
            command.ExecuteNonQuery();
            if (!readOnly)
            {
                command.CommandText = "PRAGMA synchronous=FULL;";
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
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
