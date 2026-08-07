using System.Text.Json;

namespace NexusPipeline;

internal static class ConfigStore
{
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
                Logger.Warn($"[警告] 解析 settings.json 失败，使用默认设置：{ex.Message}");
            }
        }
        Normalize(settings);
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(AppPaths.ConfigDir);
        JsonUtil.WriteAtomic(AppPaths.ConfigPath, JsonSerializer.Serialize(settings, JsonOpts.Indented));
    }

    private static void Normalize(AppSettings settings)
    {
        if (settings.HistoryRetentionDays < 1)
        {
            settings.HistoryRetentionDays = 3;
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
