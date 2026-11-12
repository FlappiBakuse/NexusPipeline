using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class StartupDatabaseRead : IDisposable
{
    private readonly List<FileStream> _inputs = [];
    internal SqliteConnection Connection { get; }

    internal StartupDatabaseRead(string path)
    {
        Connection = new();
        try
        {
            var hasWal = false;
            foreach (var suffix in new[] { "", "-wal", "-journal" })
            {
                var file = PhysicalPathPolicy.NormalizeWithoutLinks(path + suffix);
                if (suffix.Length > 0 && !File.Exists(file)) continue;
                var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _inputs.Add(input);
                if (suffix == "-wal" && input.Length > 0) hasWal = true;
                if (suffix == "-journal" && input.Length > 0)
                    throw new IOException("database_rollback_journal_requires_readonly_diagnosis");
            }
            Connection.ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = hasWal ? (OperatingSystem.IsWindows() ? PhysicalPathPolicy.NativePath(path) : path) : new Uri(path).AbsoluteUri + "?immutable=1",
                Mode = hasWal ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private, Pooling = false
            }.ToString();
            // Windows needs write-capable locks for the exclusive heap WAL index; SQL and close-time writes stay disabled.
            Connection.Open();
            if (raw.sqlite3_db_config(Connection.Handle, raw.SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE, 1, out var enabled) != raw.SQLITE_OK || enabled != 1)
                throw new IOException("startup_presentation_query_only_unavailable");
            using var command = Connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON; PRAGMA locking_mode=EXCLUSIVE; PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        try { Connection.Dispose(); }
        finally { foreach (var input in _inputs) input.Dispose(); _inputs.Clear(); }
    }
}
