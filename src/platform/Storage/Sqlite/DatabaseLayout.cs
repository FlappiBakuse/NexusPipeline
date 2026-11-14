namespace NexusPipeline.Platform.Storage.Sqlite;

[Flags]
internal enum DatabaseDomain
{
    Runtime = 1, Automation = 2, Configuration = 4, Records = 8,
    Notifications = 16, Extensions = 32, System = 64, Content = 128, All = 255
}

internal static class DatabaseLayout
{
    internal static readonly DatabaseDomain[] Domains = [DatabaseDomain.Runtime, DatabaseDomain.Automation,
        DatabaseDomain.Configuration, DatabaseDomain.Records, DatabaseDomain.Notifications,
        DatabaseDomain.Extensions, DatabaseDomain.System, DatabaseDomain.Content];

    internal static string Name(DatabaseDomain domain) => domain switch
    {
        DatabaseDomain.Runtime => "runtime", DatabaseDomain.Automation => "automation",
        DatabaseDomain.Configuration => "configuration", DatabaseDomain.Records => "records",
        DatabaseDomain.Notifications => "notifications", DatabaseDomain.Extensions => "extensions",
        DatabaseDomain.System => "system", DatabaseDomain.Content => "content",
        _ => throw new ArgumentException("database_domain_invalid")
    };

    internal static string Alias(DatabaseDomain domain) => domain == DatabaseDomain.Runtime ? "main" : Name(domain);

    internal static DatabaseDomain Parse(string name) => name == "main" ? DatabaseDomain.Runtime :
        Domains.SingleOrDefault(domain => Name(domain) == name) is var found && found != 0 ? found :
        throw new IOException("database_domain_unknown");

    internal static string PathFor(string coordinator, DatabaseDomain domain) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(coordinator))!, Name(domain) + ".db");

    internal static string RuntimePath(RuntimeInstance instance) => Path.Combine(instance.RuntimeRoot, "state", "runtime.db");

    internal static DatabaseDomain Normalize(DatabaseDomain domains)
    {
        if ((domains & ~DatabaseDomain.All) != 0) throw new ArgumentException("database_domain_invalid");
        return domains | DatabaseDomain.Runtime;
    }

    internal static void RequireFiles(string coordinator)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(coordinator))!;
        if (File.Exists(Path.Combine(directory, "nexus.db")))
            throw new IOException("legacy_database_layout_requires_new_instance");
        foreach (var domain in Domains)
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(PathFor(coordinator, domain));
            if (!File.Exists(path)) throw new IOException("database_set_incomplete");
            if (File.Exists(path + "-wal") || File.Exists(path + "-shm"))
                throw new IOException("database_set_journal_unrecognized");
        }
    }

    internal static void RequireInstance(StateDatabase database, RuntimeInstance instance)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, RuntimePath(instance)))
            throw new IOException("database_instance_unbound");
    }
}
