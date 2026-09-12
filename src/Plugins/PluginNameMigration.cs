using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>当前插件机器名迁移表。机器名是持久化契约，迁移集中在宿主启动阶段执行。</summary>
internal static class PluginNameMigration
{
    internal const string LegacyMaaStellaSora = "maastellasora";
    internal const string MaaStellaSora = "maas";

    internal static string Canonicalize(string? name)
    {
        string value = name?.Trim() ?? "";
        return value.Equals(LegacyMaaStellaSora, StringComparison.OrdinalIgnoreCase)
            ? MaaStellaSora
            : value;
    }

    internal static string? LegacyNameFor(string canonicalName)
    {
        return canonicalName.Equals(MaaStellaSora, StringComparison.OrdinalIgnoreCase)
            ? LegacyMaaStellaSora
            : null;
    }

    internal static void Apply(RuntimeContext ctx)
    {
        MigrateSettings(ctx);
        MigrateScripts(ctx);
        MigratePluginFiles();
    }

    private static void MigrateSettings(RuntimeContext ctx)
    {
        AppSettings candidate = ctx.Settings.Clone();
        Dictionary<string, PluginPreference> preferences = candidate.PluginPreferences;
        string? legacyKey = preferences.Keys.FirstOrDefault(key =>
            key.Equals(LegacyMaaStellaSora, StringComparison.OrdinalIgnoreCase));
        if (legacyKey is null)
        {
            return;
        }

        bool canonicalExists = preferences.Keys.Any(key =>
            key.Equals(MaaStellaSora, StringComparison.OrdinalIgnoreCase));
        if (!canonicalExists)
        {
            preferences[MaaStellaSora] = preferences[legacyKey];
        }
        else
        {
            Logger.Warn("[插件迁移] maas 与 maastellasora 同时存在插件偏好，按 canonical 名称 maas 保留设置。");
        }

        foreach (string key in preferences.Keys
                     .Where(key => key.Equals(LegacyMaaStellaSora, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            preferences.Remove(key);
        }

        try
        {
            ConfigStore.Save(candidate);
            ctx.ReplaceSettings(candidate);
            Logger.Info("[插件迁移] 已将插件偏好 maastellasora 迁移为 maas。");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件迁移] 插件偏好迁移落盘失败，将在下次启动重试：{ex.Message}");
        }
    }

    private static void MigrateScripts(RuntimeContext ctx)
    {
        ctx.EntityState.Mutate(state =>
        {
            List<ScriptInstance> migrated = state.Scripts.Select(script => script.Clone()).ToList();
            int changed = 0;
            foreach (ScriptInstance script in migrated)
            {
                if (!string.Equals(script.PluginType, LegacyMaaStellaSora, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                script.PluginType = MaaStellaSora;
                changed++;
            }
            if (changed == 0)
            {
                return;
            }

            try
            {
                DataStore.SaveScripts(migrated);
                state.Scripts.Clear();
                state.Scripts.AddRange(migrated);
                Logger.Info($"[插件迁移] 已将 {changed} 个脚本实例的插件绑定迁移为 maas。");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件迁移] 脚本插件绑定迁移落盘失败，将在下次启动重试：{ex.Message}");
            }
        });
    }

    private static void MigratePluginFiles()
    {
        string pluginsRoot = Path.Combine(AppPaths.ConfigDir, "plugins");
        if (!Directory.Exists(pluginsRoot))
        {
            return;
        }

        MoveDirectoryIfNeeded(
            Path.Combine(pluginsRoot, LegacyMaaStellaSora),
            Path.Combine(pluginsRoot, MaaStellaSora));
        MoveFileIfNeeded(
            Path.Combine(pluginsRoot, LegacyMaaStellaSora + ".json"),
            Path.Combine(pluginsRoot, MaaStellaSora + ".json"));
        MoveFileIfNeeded(
            Path.Combine(pluginsRoot, LegacyMaaStellaSora + ".secrets.json"),
            Path.Combine(pluginsRoot, MaaStellaSora + ".secrets.json"));
    }

    private static void MoveDirectoryIfNeeded(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            Logger.Warn("[插件迁移] maastellasora 与 maas 的插件作用域目录同时存在，保留双方目录并使用 maas。");
            return;
        }
        try
        {
            Directory.Move(source, destination);
            Logger.Info("[插件迁移] 已迁移插件作用域目录 maastellasora -> maas。");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件迁移] 插件作用域目录迁移失败，将在下次启动重试：{ex.Message}");
        }
    }

    private static void MoveFileIfNeeded(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            Logger.Warn($"[插件迁移] 插件配置文件冲突，保留 canonical 文件：{Path.GetFileName(destination)}。");
            return;
        }
        try
        {
            File.Move(source, destination);
            Logger.Info($"[插件迁移] 已迁移插件配置文件 {Path.GetFileName(source)} -> {Path.GetFileName(destination)}。");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件迁移] 插件配置文件迁移失败，将在下次启动重试：{ex.Message}");
        }
    }
}
