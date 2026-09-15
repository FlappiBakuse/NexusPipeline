using NexusPipeline.Extensibility;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

internal sealed partial class DataSpecializedPlugin
{
    public static DataSpecializedPlugin? Load(string pluginDir)
    {
        if (!PluginManifest.TryLoad(pluginDir, out PluginManifest? manifest, out _)
            || manifest is null
            || manifest.Kind != "data-specialized")
        {
            return null;
        }
        return Load(pluginDir, manifest);
    }

    /// <summary>使用发现阶段已解析的 manifest 加载数据插件，避免重复读取和解释 plugin.json。</summary>
    internal static DataSpecializedPlugin? Load(string pluginDir, PluginManifest manifest)
    {
        try
        {
            var plugin = new DataSpecializedPlugin
            {
                PluginDirectory = Path.GetFullPath(pluginDir),
                Name = manifest.Name,
                ArtifactName = manifest.ArtifactName,
                SchemaVersion = manifest.SchemaVersion,
                DisplayName = manifest.DisplayName,
                GameName = manifest.GameName,
                Description = manifest.Description,
                Version = manifest.Version,
                MinHostVersion = manifest.MinHostVersion,
                Frontend = manifest.Frontend,
                Localization = manifest.Localization,
                _resolvePath = manifest.ResolvePath,
                _judgeScriptPath = manifest.JudgeScriptPath,
                _configValidatorPath = manifest.ConfigValidatorPath,
                _configEditorPath = manifest.ConfigEditorPath,
            };
            foreach (string capability in manifest.Capabilities)
            {
                plugin._capabilityKeys.Add(capability);
            }
            if (string.IsNullOrWhiteSpace(plugin.Name) || string.IsNullOrWhiteSpace(plugin._resolvePath) || string.IsNullOrWhiteSpace(plugin._judgeScriptPath))
            {
                return null;
            }
            if (!IsSafeRelativePath(plugin._resolvePath) || !IsSafeRelativePath(plugin._judgeScriptPath))
            {
                return null;
            }
            plugin._resolvePath = Path.Combine(pluginDir, plugin._resolvePath);
            plugin._judgeScriptPath = Path.Combine(pluginDir, plugin._judgeScriptPath);
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
                plugin._configValidatorPath = Path.Combine(pluginDir, plugin._configValidatorPath);
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
                plugin._configEditorPath = Path.Combine(pluginDir, plugin._configEditorPath);
                if (!File.Exists(plugin._configEditorPath))
                {
                    return null;
                }
            }
            return plugin;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 加载数据化插件 {Path.GetFileName(pluginDir)} 失败：{ex.Message}");
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
