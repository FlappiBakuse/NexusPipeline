using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

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
                AppSettings? parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.ConfigPath), JsonOpts.Default);
                if (parsed is not null)
                {
                    settings = parsed;
                }
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
        if (settings.SendStrategy is not ("parallel" or "webhook_primary" or "email_primary" or "single"))
        {
            settings.SendStrategy = "parallel";
        }
        // v0.7.0+：旧配置升级时补默认启用的内置插件（模拟器适配），保证升级后默认可用；
        // 已在 DisabledPlugins 显式禁用的不补回（SetEnabled 禁用会写入 DisabledPlugins）。
        if (!settings.EnabledPlugins.Contains(AppSettings.EmulatorAdapterPlugin, StringComparer.OrdinalIgnoreCase)
            && !settings.DisabledPlugins.Contains(AppSettings.EmulatorAdapterPlugin, StringComparer.OrdinalIgnoreCase))
        {
            settings.EnabledPlugins.Add(AppSettings.EmulatorAdapterPlugin);
        }
        if (settings.WebhookType is not ("feishu" or "dingtalk" or "wecom" or "slack" or "discord" or "generic"))
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
}
