using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class PendingCapacityProjection(string name, string key, string pendingRows,
    params PendingCapacityProjection.Dependency[] dependencies)
{
    internal sealed record Dependency(string Table, string Key, string? Lookup = null, string? MembershipColumn = null)
    {
        internal string AffectedKeys(string row) => (Lookup ?? "SELECT ") + $"{row}.{Key}";
        internal string WhenAffected(string row) => Lookup is null ? "" : $"WHEN EXISTS ({AffectedKeys(row)})";
        internal string UpdateColumns => MembershipColumn is null ? Key : $"{Key}, {MembershipColumn}";
        internal string MembershipChanged => MembershipColumn is null ? "" : $" OR OLD.{MembershipColumn} IS NOT NEW.{MembershipColumn}";
    }

    internal long CountInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        var (version, configured) = ReadVersion(connection, transaction);
        if (!configured) Configure(connection, transaction);

        using var count = DatabaseConnections.Command(connection, transaction, $"""
            SELECT (SELECT count(*) FROM temp.{name})
            FROM temp.{name}_version WHERE data_version=$0;
            """, version);
        if (count.ExecuteScalar() is long current) return current;

        // The caller holds a runtime snapshot or write reservation. This handle's triggers track its own changes;
        // a new data_version indicates a commit by another handle and invalidates the projection.
        using var refresh = DatabaseConnections.Command(connection, transaction, $"""
            DELETE FROM temp.{name};
            INSERT INTO temp.{name} SELECT candidate.{key} {pendingRows};
            DELETE FROM temp.{name}_version;
            INSERT INTO temp.{name}_version VALUES($0);
            """, version);
        refresh.ExecuteNonQuery();
        return (long)count.ExecuteScalar()!;
    }

    private (long Version, bool Configured) ReadVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = DatabaseConnections.Command(connection, transaction, """
            SELECT data_version, EXISTS(SELECT 1 FROM temp.sqlite_schema WHERE type='table' AND name=$0)
            FROM pragma_data_version();
            """, name + "_version");
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new IOException("pending_capacity_version_missing");
        return (row.GetInt64(0), row.GetBoolean(1));
    }

    private void Configure(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var create = DatabaseConnections.Command(connection, transaction, $"""
            CREATE TEMP TABLE {name}({key} TEXT PRIMARY KEY) STRICT, WITHOUT ROWID;
            CREATE TEMP TABLE {name}_version(data_version INTEGER NOT NULL) STRICT;
            """);
        create.ExecuteNonQuery();
        foreach (var dependency in dependencies)
        {
            var table = dependency.Table;
            var changedKey = dependency.Key;
            var removed = Reconcile(dependency.AffectedKeys("OLD"));
            var added = Reconcile(dependency.AffectedKeys("NEW"));
            using var triggers = DatabaseConnections.Command(connection, transaction, $"""
                CREATE TEMP TRIGGER {name}_insert_{table} AFTER INSERT ON main.{table}
                {dependency.WhenAffected("NEW")}
                BEGIN {added} END;
                CREATE TEMP TRIGGER {name}_delete_{table} AFTER DELETE ON main.{table}
                {dependency.WhenAffected("OLD")}
                BEGIN {removed} END;
                CREATE TEMP TRIGGER {name}_update_{table} AFTER UPDATE OF {dependency.UpdateColumns} ON main.{table}
                WHEN OLD.{changedKey} IS NOT NEW.{changedKey}{dependency.MembershipChanged}
                BEGIN {removed} {added} END;
                """);
            triggers.ExecuteNonQuery();
        }
    }

    private string Reconcile(string keys) => $"""
        DELETE FROM {name} WHERE {key} IN ({keys});
        INSERT INTO {name} SELECT candidate.{key} {pendingRows} AND candidate.{key} IN ({keys});
        """;
}
