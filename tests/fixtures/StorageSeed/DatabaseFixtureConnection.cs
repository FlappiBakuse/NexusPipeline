using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.TestFixtures;

internal static class DatabaseFixtureConnection
{
    internal static SqliteConnection Open(string value)
    {
        var settings = new SqliteConnectionStringBuilder(value);
        var path = settings.DataSource;
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal)) path = "\\\\" + path[8..];
        else if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path[4..];
        var runtime = Path.GetFileName(path) == "runtime.db";
        if (runtime) settings.DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        var connection = new SqliteConnection(settings.ToString());
        try
        {
            connection.Open();
            if (!runtime) return connection;
            if (DatabaseLayout.Domains.Any(domain => !File.Exists(DatabaseLayout.PathFor(path, domain))))
                return connection;
            foreach (var domain in DatabaseLayout.Domains.Where(domain => domain != DatabaseDomain.Runtime))
            {
                var member = DatabaseLayout.PathFor(path, domain);
                using var command = connection.CreateCommand();
                command.CommandText = "ATTACH DATABASE $path AS " + DatabaseLayout.Alias(domain) + ";";
                command.Parameters.AddWithValue("$path", settings.Mode == SqliteOpenMode.ReadOnly
                    ? new Uri(member).AbsoluteUri + "?mode=ro" : member);
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
