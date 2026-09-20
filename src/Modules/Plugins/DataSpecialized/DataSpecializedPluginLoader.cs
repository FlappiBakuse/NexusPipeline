using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Plugins.DataSpecialized;

/// <summary>负责加载并校验 data-specialized 插件的 manifest 和脚本路径。</summary>
internal static class DataSpecializedPluginLoader
{
    internal static DataSpecializedPlugin? Load(string pluginDir)
    {
        if (!PluginManifest.TryLoad(pluginDir, out PluginManifest? manifest, out _)
            || manifest is null
            || manifest.Kind != "data-specialized")
        {
            return null;
        }
        return Load(pluginDir, manifest);
    }

    internal static DataSpecializedPlugin? Load(string pluginDir, PluginManifest manifest)
    {
        try
        {
            if (!SpecializedPluginContract.TryValidatePayload(pluginDir, out _))
            {
                return null;
            }
            var plugin = new DataSpecializedPlugin(manifest, pluginDir);
            if (string.IsNullOrWhiteSpace(plugin.Name)
                || string.IsNullOrWhiteSpace(plugin._resolvePath)
                || string.IsNullOrWhiteSpace(plugin._judgeScriptPath))
            {
                return null;
            }
            if (!IsSafeRelativePath(plugin._resolvePath)
                || !IsSafeRelativePath(plugin._judgeScriptPath))
            {
                return null;
            }
            plugin._resolvePath = Path.Combine(plugin.PluginDirectory, plugin._resolvePath);
            plugin._judgeScriptPath = Path.Combine(plugin.PluginDirectory, plugin._judgeScriptPath);
            if (!File.Exists(plugin._resolvePath) || !File.Exists(plugin._judgeScriptPath))
            {
                return null;
            }
            if (plugin._configValidatorPath is not null)
            {
                if (!IsSafeRelativePath(plugin._configValidatorPath)
                    || !plugin._configValidatorPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                plugin._configValidatorPath = Path.Combine(plugin.PluginDirectory, plugin._configValidatorPath);
                if (!File.Exists(plugin._configValidatorPath))
                {
                    return null;
                }
            }
            if (plugin._configEditorPath is not null)
            {
                if (!IsSafeRelativePath(plugin._configEditorPath)
                    || !plugin._configEditorPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                plugin._configEditorPath = Path.Combine(plugin.PluginDirectory, plugin._configEditorPath);
                if (!File.Exists(plugin._configEditorPath))
                {
                    return null;
                }
            }
            return plugin;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 加载数据专用插件 {Path.GetFileName(pluginDir)} 失败：{ex.Message}");
            return null;
        }
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || Path.IsPathRooted(value))
        {
            return false;
        }
        string normalized = value.Replace('\\', '/');
        return !normalized.Contains(':', StringComparison.Ordinal)
            && !normalized.Split('/').Any(part => part is "" or "." or "..");
    }
}
