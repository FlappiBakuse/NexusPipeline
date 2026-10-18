using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins.Runtime;

internal sealed record PluginNotificationMigrationDocument(string Path, byte[] Before, byte[] After);

internal sealed partial class PluginManager
{
    internal IReadOnlyList<PluginNotificationMigrationDocument> PrepareNotificationMigrationDocuments(Func<string, IReadOnlyList<string>> resolve)
    {
        var result = new List<PluginNotificationMigrationDocument>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in _discovery.DiscoverManagedPlugins(new HashSet<string>()))
        {
            if (descriptor.Manifest.ApiVersion != "2.3") continue;
            ManagedPluginContract.Validate(descriptor.Directory, descriptor.Manifest);
            string entry = Path.GetFullPath(Path.Combine(descriptor.Directory, descriptor.Manifest.EntryAssembly));
            PayloadPathSafety.RequireLinkFree(entry);
            if (!entry.StartsWith(Path.GetFullPath(descriptor.Directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("plugin_notification_migration_path");
            var context = new PluginLoadContext(entry);
            try
            {
                var assembly = context.LoadEntryAssembly(entry);
                var type = assembly.GetType(descriptor.Manifest.EntryType, true)!;
                if (!typeof(IPluginNotificationDataMigration).IsAssignableFrom(type)) continue;
                var converter = (IPluginNotificationDataMigration)Activator.CreateInstance(type)!;
                foreach (string scope in converter.NotificationDataScopes)
                {
                    string[] segments = PluginScopedDataStore.NormalizeScope(scope);
                    string path = Path.Combine(new[] { AppPaths.ConfigDir, "plugins", descriptor.Manifest.Name, "scopes" }.Concat(segments).ToArray()) + ".json";
                    PayloadPathSafety.RequireLinkFree(path);
                    if (!File.Exists(path)) continue;
                    byte[] before = File.ReadAllBytes(path);
                    var source = JsonNode.Parse(Encoding.UTF8.GetString(before).TrimStart('\uFEFF')) as JsonObject ?? throw new InvalidDataException("plugin_notification_migration_schema");
                    JsonObject output = converter.MigrateNotificationData(scope, source, resolve);
                    result.Add(new(path, before, Encoding.UTF8.GetBytes(output.ToJsonString(JsonOpts.Indented))));
                    handled.Add(Path.GetFullPath(path));
                }
            }
            finally { context.Unload(); }
        }
        // This registered legacy store needs its owner's converter even when the plugin is disabled or absent.
        string checkIn = Path.Combine(AppPaths.ConfigDir, "plugins", "game-checkin", "scopes", "tasks-v2", "task-store.json");
        if (File.Exists(checkIn) && !handled.Contains(Path.GetFullPath(checkIn)))
            throw new InvalidDataException("notification_migration_plugin_unavailable");
        return result;
    }
}
