using NexusPipeline.Plugins.Managed;

namespace NexusPipeline.Plugins;

internal sealed partial class DataSpecializedPlugin
{
    public static DataSpecializedPlugin? Load(string pluginDir) =>
        DataSpecializedPluginLoader.Load(pluginDir);

    internal static DataSpecializedPlugin? Load(string pluginDir, PluginManifest manifest) =>
        DataSpecializedPluginLoader.Load(pluginDir, manifest);
}
