using NexusPipeline.Core.Settings;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed record StartupPresentation(DesktopPreferences Desktop, HostLocale Locale)
{
    public static StartupPresentation Read(RuntimeInstance instance, InstanceLease lease)
    {
        lease.VerifyHeld(instance);
        var path = DatabaseLayout.RuntimePath(instance);
        if (File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "nexus.db"))) DatabaseLayout.RequireFiles(path);
        var guard = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "state-initialization.guard"));
        if (!File.Exists(guard) && !File.Exists(path)) return new(HostSettings.Default.Desktop, HostSettings.Default.Locale);
        var database = StateDatabase.OpenCurrent(instance, lease);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        var settings = HostSettingsRows.ReadCurrent(connection, transaction).Values;
        transaction.Commit();
        return new(settings.Desktop, settings.Locale);
    }
}
