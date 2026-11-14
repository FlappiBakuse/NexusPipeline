using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class DatabaseChanges : IDisposable
{
    private readonly SqliteConnection _connection;
    internal DatabaseDomain Domains { get; private set; }
    internal Dictionary<string, HashSet<long>> Rows { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> DeletedParents { get; } = new(StringComparer.Ordinal);

    internal DatabaseChanges(SqliteConnection connection)
    {
        _connection = connection;
        SQLitePCL.raw.sqlite3_update_hook(connection.Handle!, (_, operation, database, table, rowId) =>
        {
            var name = database.utf8_to_string();
            if (name == "temp") return;
            Domains |= DatabaseLayout.Parse(name);
            var target = table.utf8_to_string();
            if (operation == SQLitePCL.raw.SQLITE_DELETE)
            {
                if (DatabaseRelations.TracksParent(target)) DeletedParents.Add(target);
            }
            else if (DatabaseRelations.TracksChild(target))
            {
                if (!Rows.TryGetValue(target, out var rows)) Rows.Add(target, rows = []);
                rows.Add(rowId);
            }
        }, null);
    }

    public void Dispose() => SQLitePCL.raw.sqlite3_update_hook(_connection.Handle!, (SQLitePCL.delegate_update?)null, null);
}
