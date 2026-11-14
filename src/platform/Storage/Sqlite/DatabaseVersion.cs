using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal readonly record struct DatabaseVersion(long Runtime, long Automation, long Configuration, long Records,
    long Notifications, long Extensions, long System, long Content)
{
    internal static DatabaseVersion Read(StateDatabase database, SqliteConnection? connection, DatabaseDomain domains)
    {
        long Version(DatabaseDomain domain) => (domains & domain) == 0 ? 0 : connection is null
            ? database.DomainSequence(domain)
            : (long)DatabaseConnections.Scalar(connection, $"PRAGMA {DatabaseLayout.Alias(domain)}.data_version;");
        return new(Version(DatabaseDomain.Runtime), Version(DatabaseDomain.Automation), Version(DatabaseDomain.Configuration),
            Version(DatabaseDomain.Records), Version(DatabaseDomain.Notifications), Version(DatabaseDomain.Extensions),
            Version(DatabaseDomain.System), Version(DatabaseDomain.Content));
    }
}
