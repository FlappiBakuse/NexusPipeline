using NexusPipeline.Core.Settings;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed record StartupPresentation(DesktopPreferences Desktop, HostLocale Locale)
{
    public static StartupPresentation Read(RuntimeInstance instance, InstanceLease lease)
    {
        lease.VerifyHeld(instance);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "state", "nexus.db"));
        var guard = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "state-initialization.guard"));
        if (!File.Exists(guard) && !File.Exists(path))
            return new(HostSettings.Default.Desktop, HostSettings.Default.Locale);
        if (!File.Exists(guard) || new FileInfo(guard).Length != 0 || !File.Exists(path))
            throw new IOException("startup_presentation_database_incomplete");
        using var reading = new StartupDatabaseRead(path);
        var connection = reading.Connection;
        using var transaction = connection.BeginTransaction(deferred: true);
        DatabaseInspector.VerifyIdentity(connection, instance.InstanceId, transaction: transaction);
        var settings = HostSettingsRows.ReadCurrent(connection, transaction).Values;
        return new(settings.Desktop, settings.Locale);
    }
}
