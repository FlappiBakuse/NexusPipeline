using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>发现 data-specialized 和 managed-code 插件，并读取启用偏好。</summary>
internal sealed class PluginDiscovery
{
    private readonly Func<AppSettings> _settings;

    internal PluginDiscovery(Func<AppSettings> settings)
    {
        _settings = settings;
    }

    internal bool ReadConfiguredEnabled(string name, bool managedCode)
    {
        AppSettings settings = _settings();
        PluginPreference? preference = settings.PluginPreferences?
            .FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        return preference?.Enabled ?? !managedCode;
    }

    internal IReadOnlyList<ManagedPluginDescriptor> DiscoverManagedPlugins(
        IReadOnlySet<string> knownNames)
    {
        var descriptors = new List<ManagedPluginDescriptor>();
        if (!Directory.Exists(AppPaths.PluginsDir))
        {
            return descriptors;
        }
        foreach (string directory in Directory.GetDirectories(AppPaths.PluginsDir))
        {
            if (!PluginManifest.TryLoad(directory, out PluginManifest? manifest, out string? error) || manifest is null)
            {
                Logger.Warn($"[插件] 忽略无效插件目录：{Path.GetFileName(directory)}（{error}）");
                continue;
            }
            if (manifest.Kind != "managed-code")
            {
                continue;
            }
            if (!string.Equals(Path.GetFileName(directory), manifest.ArtifactName, StringComparison.Ordinal))
            {
                Logger.Error($"[插件] 插件目录名称不匹配：{Path.GetFileName(directory)}（期望 {manifest.ArtifactName}）");
                continue;
            }
            if (knownNames.Contains(manifest.Name)
                || descriptors.Any(item => string.Equals(item.Manifest.Name, manifest.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Warn($"[插件] 检测到重复插件名：{manifest.Name}，忽略 managed-code 插件");
                continue;
            }
            descriptors.Add(new ManagedPluginDescriptor(manifest, directory));
        }
        return descriptors;
    }

    internal List<DataSpecializedPlugin> DiscoverDataPlugins()
    {
        var list = new List<DataSpecializedPlugin>();
        if (!Directory.Exists(AppPaths.PluginsDir))
        {
            return list;
        }
        foreach (string directory in Directory.GetDirectories(AppPaths.PluginsDir))
        {
            if (!PluginManifest.TryLoad(directory, out PluginManifest? manifest, out _)
                || manifest is null
                || manifest.Kind != "data-specialized")
            {
                continue;
            }
            if (!string.Equals(Path.GetFileName(directory), manifest.ArtifactName, StringComparison.Ordinal))
            {
                Logger.Error($"[插件] 插件目录名称不匹配：{Path.GetFileName(directory)}（期望 {manifest.ArtifactName}）");
                continue;
            }
            DataSpecializedPlugin? plugin = DataSpecializedPlugin.Load(directory, manifest);
            if (plugin is not null)
            {
                list.Add(plugin);
            }
            else
            {
                Logger.Warn($"[插件] 忽略无效数据专用插件目录：{Path.GetFileName(directory)}");
            }
        }
        return list;
    }
}
