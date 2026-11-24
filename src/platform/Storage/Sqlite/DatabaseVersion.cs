using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal readonly record struct DatabaseVersion(long Runtime, long Automation, long Configuration, long Records,
    long Notifications, long Extensions, long System, long Content)
{
    internal static DatabaseVersion Read(StateDatabase database, SqliteConnection? connection, DatabaseDomain domains,
        SqliteTransaction? transaction = null)
    {
        long Version(DatabaseDomain domain)
        {
            if ((domains & domain) == 0) return 0;
            if (connection is null) return database.DomainSequence(domain);
            using var command = DatabaseConnections.Command(connection, transaction, $"PRAGMA {DatabaseLayout.Alias(domain)}.data_version;");
            return (long)command.ExecuteScalar()!;
        }
        return new(Version(DatabaseDomain.Runtime), Version(DatabaseDomain.Automation), Version(DatabaseDomain.Configuration),
            Version(DatabaseDomain.Records), Version(DatabaseDomain.Notifications), Version(DatabaseDomain.Extensions),
            Version(DatabaseDomain.System), Version(DatabaseDomain.Content));
    }
}
