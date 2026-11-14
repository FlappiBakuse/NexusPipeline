using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class DatabaseConnections
{
    private sealed class ConnectionState
    {
        internal DatabaseDomain Domains = DatabaseDomain.Runtime;
        internal bool Initialized;
    }
    private static readonly ConditionalWeakTable<SQLitePCL.sqlite3, ConnectionState> States = new();

    internal static SqliteConnection Open(string path, bool readOnly = false, bool allowPooling = true,
        DatabaseDomain domains = DatabaseDomain.All, bool creating = false)
    {
        using var timing = StartupProfile.Measure("database.open");
        domains = DatabaseLayout.Normalize(domains);
        DatabaseConnectionPool.BorrowLease? shared = null;
        SqliteConnection? connection = null;
        try
        {
            var protectedPath = readOnly ? DatabaseReadScope.ProtectedPath(path) : null;
            if (allowPooling) shared = DatabaseConnectionPool.Borrow(path, domains: domains, readOnly: readOnly);
            connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = new Uri(Path.GetFullPath(shared?.Path ?? protectedPath ?? path)).AbsoluteUri +
                    "?nexus_domains=" + ((int)domains).ToString(System.Globalization.CultureInfo.InvariantCulture) + (shared?.PoolSuffix ?? ""),
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private, Pooling = shared?.IsPooled ?? false,
                DefaultTimeout = 2
            }.ToString());
            connection.Open();
            Configure(connection, path, domains, readOnly, creating);
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

    internal static SqliteConnection OpenFrozen(string path)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1";
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static void Configure(SqliteConnection connection, string path, DatabaseDomain domains, bool readOnly, bool creating)
    {
        var state = States.GetValue(connection.Handle!, _ => new());
        using var command = connection.CreateCommand();
        var changed = !state.Initialized;
        if (!state.Initialized)
        {
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA temp_store=MEMORY;";
            command.ExecuteNonQuery();
            if (!readOnly) ConfigureWriter(command, "main");
            state.Initialized = true;
        }
        foreach (var domain in DatabaseLayout.Domains.Where(domain => (domains & domain) != 0 && (state.Domains & domain) == 0))
        {
            var file = PhysicalPathPolicy.NormalizeWithoutLinks(DatabaseLayout.PathFor(path, domain));
            if (!File.Exists(file)) throw new IOException("database_set_incomplete");
            var alias = DatabaseLayout.Alias(domain);
            command.CommandText = "ATTACH DATABASE $path AS " + alias + ";";
            command.Parameters.AddWithValue("$path", readOnly ? new Uri(file).AbsoluteUri + "?mode=ro" : PhysicalPathPolicy.NativePath(file));
            command.ExecuteNonQuery(); command.Parameters.Clear();
            if (!readOnly) ConfigureWriter(command, alias);
            state.Domains |= domain; changed = true;
        }
        if (!changed) return;
        var selected = DatabaseLayout.Domains.Where(domain => (state.Domains & domain) != 0).ToArray();
        int Weight(DatabaseDomain domain) => selected.Length <= 3 ? 1 : domain switch
        { DatabaseDomain.Runtime => 4, DatabaseDomain.Configuration => 3, _ => 1 };
        var unit = 8192 / selected.Sum(Weight);
        foreach (var domain in DatabaseLayout.Domains.Where(domain => (state.Domains & domain) != 0))
        {
            command.CommandText = $"PRAGMA {DatabaseLayout.Alias(domain)}.cache_size=-{Math.Max(64, unit * Weight(domain))};";
            command.ExecuteNonQuery();
        }
        if (!readOnly && !creating) DatabaseRelations.Configure(connection);
        if (readOnly)
        {
            command.CommandText = "PRAGMA query_only=ON;"; command.ExecuteNonQuery();
        }
    }

    private static void ConfigureWriter(SqliteCommand command, string alias)
    {
        command.CommandText = $"PRAGMA {alias}.journal_mode=PERSIST;";
        if (!string.Equals(Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture), "persist", StringComparison.Ordinal))
            throw new IOException("database_rollback_mode_unavailable");
        command.CommandText = $"PRAGMA {alias}.synchronous=FULL;"; command.ExecuteNonQuery();
    }

    internal static DatabaseDomain Domains(SqliteConnection connection) =>
        connection.Handle is { } handle && States.TryGetValue(handle, out var state) ? state.Domains : DatabaseDomain.Runtime;

    internal static bool IsBound(SqliteConnection connection, string path)
    {
        var filename = SQLitePCL.raw.sqlite3_db_filename(connection.Handle!, "main").utf8_to_string();
        return filename is not null && StringComparer.OrdinalIgnoreCase.Equals(
            PhysicalPathPolicy.NormalizeSyntax(filename), PhysicalPathPolicy.NormalizeSyntax(path));
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params object[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$" + index, values[index]);
        return command;
    }

    internal static object Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar() ?? throw new IOException("Missing database metadata");
    }

    internal static void RejectLinks(string path) => PhysicalPathPolicy.NormalizeWithoutLinks(path);
}
