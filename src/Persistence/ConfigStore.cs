using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Persistence;

internal static class ConfigStore
{
    public static AppSettings Load(ConfigLoadMode mode = ConfigLoadMode.Repair)
    {
        var settings = new AppSettings();
        if (File.Exists(AppPaths.ConfigPath))
        {
            try
            {
                string text = File.ReadAllText(AppPaths.ConfigPath);
                AppSettings? parsed = JsonSerializer.Deserialize<AppSettings>(text, JsonOpts.Default);
                if (parsed is not null)
                {
                    settings = parsed;
                }
            }
            catch (Exception ex)
            {
                if (mode == ConfigLoadMode.Repair)
                {
                    string backup = JsonStore.PreserveCorruptFile(AppPaths.ConfigPath);
                    Logger.Warn($"[警告] 解析 settings.json 失败，使用默认设置：{ex.Message}，原文件已保留为 {Path.GetFileName(backup)}（可手动恢复，不再被后续保存覆盖）");
                }
                else
                {
                    Logger.Warn($"[警告] 解析 settings.json 失败，使用默认设置：{ex.Message}（只读启动不修改原文件）");
                }
            }
        }
        Normalize(settings);
        Logger.ConfigureLevel(settings.LogLevel);
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(AppPaths.ConfigDir);
        JsonUtil.WriteAtomic(AppPaths.ConfigPath, JsonSerializer.Serialize(settings, JsonOpts.Indented));
        // 原子保存成功后才更新日志阈值，失败时保留旧阈值。
        Logger.ConfigureLevel(settings.LogLevel);
    }

    private static void Normalize(AppSettings settings)
    {
        if (settings.HistoryRetentionDays < 1 || settings.HistoryRetentionDays > AppFixedLimits.HistoryRetentionDaysMax)
        {
            settings.HistoryRetentionDays = 7;
        }
        if (settings.WebPort < 1024 || settings.WebPort > 65535)
        {
            settings.WebPort = 58731;
        }
        if (settings.McpPort < 1024 || settings.McpPort > 65535)
        {
            settings.McpPort = 58732;
        }
        if (!LogLevelUtil.IsValid(settings.LogLevel))
        {
            settings.LogLevel = "info";
        }
        settings.PluginPreferences ??= new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase);
        // Webhook 类型白名单引用 AppSettings.WebhookTypes（单源），不再双份维护。
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
        settings.ProxyMode = (settings.ProxyMode ?? "none").Trim().ToLowerInvariant();
        if (settings.ProxyMode is not ("none" or "system" or "http"))
        {
            settings.ProxyMode = "none";
        }
        settings.ProxyUrl = (settings.ProxyUrl ?? "").Trim();
        settings.ProxyUsername = (settings.ProxyUsername ?? "").Trim();
        settings.ProxyPassword ??= "";
        if (!string.IsNullOrWhiteSpace(settings.ProxyUrl)
            && (!Uri.TryCreate(settings.ProxyUrl, UriKind.Absolute, out Uri? proxyUri)
                || (!string.Equals(proxyUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(proxyUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))))
        {
            settings.ProxyUrl = "";
        }
        // 更新渠道白名单（stable/prerelease）；镜像源仅校验格式，空=默认 GitHub。
        if (settings.UpdateChannel is not ("stable" or "prerelease"))
        {
            settings.UpdateChannel = "prerelease";
        }
        if (!string.IsNullOrWhiteSpace(settings.UpdateSourceUrl)
            && (!Uri.TryCreate(settings.UpdateSourceUrl.Trim(), UriKind.Absolute, out Uri? sourceUri)
                || sourceUri.Scheme != Uri.UriSchemeHttps))
        {
            settings.UpdateSourceUrl = "";
        }
    }

}
