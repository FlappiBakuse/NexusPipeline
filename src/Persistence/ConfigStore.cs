using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Utilities;
using System.Text.Json.Nodes;

namespace NexusPipeline.Persistence;

internal static class ConfigStore
{
    /// <summary>历史保留天数上限（v0.6.6+ 由 limits.json 约束，消除硬编码 180；Limits.Load 时同步，避免 Persistence 反向依赖 Services）。</summary>
    private static int _maxHistoryRetentionDays = 180;

    public static void ApplyMaxHistoryRetentionDays(int maxDays)
    {
        _maxHistoryRetentionDays = Math.Max(1, maxDays);
    }

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        if (File.Exists(AppPaths.ConfigPath))
        {
            try
            {
                string text = File.ReadAllText(AppPaths.ConfigPath);
                JsonNode? raw = JsonNode.Parse(text);
                AppSettings? parsed = JsonSerializer.Deserialize<AppSettings>(text, JsonOpts.Default);
                if (parsed is not null)
                {
                    settings = parsed;
                }
                MigrateLegacyPluginPreferences(settings, raw);
            }
            catch (Exception ex)
            {
                string backup = JsonStore.PreserveCorruptFile(AppPaths.ConfigPath);
                Logger.Warn($"[警告] 解析 settings.json 失败，使用默认设置：{ex.Message}，原文件已保留为 {Path.GetFileName(backup)}（可手动恢复，不再被后续保存覆盖）");
            }
        }
        Normalize(settings);
        // v0.6.6+：加载后刷新日志阈值缓存（即时生效）。
        Logger.RefreshLevel();
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(AppPaths.ConfigDir);
        JsonUtil.WriteAtomic(AppPaths.ConfigPath, JsonSerializer.Serialize(settings, JsonOpts.Indented));
        // v0.6.6+：保存后刷新日志阈值缓存（即时生效）。
        Logger.RefreshLevel();
    }

    private static void Normalize(AppSettings settings)
    {
        if (settings.HistoryRetentionDays < 1 || settings.HistoryRetentionDays > _maxHistoryRetentionDays)
        {
            settings.HistoryRetentionDays = 7;
        }
        if (settings.WebPort < 1024 || settings.WebPort > 65535)
        {
            settings.WebPort = 58731;
        }
        if (!LogLevelUtil.IsValid(settings.LogLevel))
        {
            settings.LogLevel = "info";
        }
        settings.PluginPreferences ??= new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase);
        settings.PluginPreferences.Remove("notify");
        settings.PluginPreferences.Remove("emulator-adapter");
        // v0.7.4（KN-26）：Webhook 类型白名单引用 AppSettings.WebhookTypes（单源），不再双份维护。
        if (!AppSettings.WebhookTypes.Contains(settings.WebhookType))
        {
            settings.WebhookType = "feishu";
        }
        if (settings.WebhookTimeout < 1)
        {
            settings.WebhookTimeout = 30;
        }
        if (settings.SmtpPort < 1 || settings.SmtpPort > 65535)
        {
            settings.SmtpPort = 465;
        }
        if (settings.SmtpSecure is not ("auto" or "ssl" or "starttls" or "none"))
        {
            settings.SmtpSecure = "auto";
        }
        if (settings.SmtpTimeout < 1)
        {
            settings.SmtpTimeout = 30;
        }
    }

    /// <summary>
    /// 将 v0.9.4 及更早版本的 EnabledPlugins/DisabledPlugins 一次性迁移为插件偏好。
    /// 通知和模拟器已成为宿主内建能力，历史开关被有意丢弃。
    /// </summary>
    private static void MigrateLegacyPluginPreferences(AppSettings settings, JsonNode? raw)
    {
        if (raw is not JsonObject root)
        {
            return;
        }
        settings.PluginPreferences ??= new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase);
        MigrateList(root["EnabledPlugins"], settings, enabled: true);
        MigrateList(root["DisabledPlugins"], settings, enabled: false);
        settings.PluginPreferences.Remove("notify");
        settings.PluginPreferences.Remove("emulator-adapter");
    }

    private static void MigrateList(JsonNode? node, AppSettings settings, bool enabled)
    {
        if (node is not JsonArray values)
        {
            return;
        }
        foreach (JsonNode? value in values)
        {
            string name = value?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name)
                || name.Equals("notify", StringComparison.OrdinalIgnoreCase)
                || name.Equals("emulator-adapter", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!settings.PluginPreferences.ContainsKey(name))
            {
                settings.PluginPreferences[name] = new PluginPreference { Enabled = enabled };
            }
        }
    }
}
