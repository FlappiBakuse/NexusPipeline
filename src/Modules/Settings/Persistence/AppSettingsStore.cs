using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Localization;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Settings.Persistence;

internal static class AppSettingsStore
{
    private static readonly HashSet<string> CurrentProperties = JsonSerializer
        .SerializeToElement(new AppSettings(), JsonOpts.Default).EnumerateObject()
        .Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static readonly HashSet<string> LegacyNotificationProperties = new(StringComparer.OrdinalIgnoreCase)
    { "webhookEnabled", "webhookScreenshotEnabled", "smtpEnabled", "smtpScreenshotEnabled", "webhookType", "webhookUrl", "webhookSecret", "webhookTemplate", "webhookTimeout", "feishuAppId", "feishuAppSecret", "slackBotToken", "slackChannelId", "smtpHost", "smtpPort", "smtpSecure", "smtpUser", "smtpPassword", "smtpFrom", "smtpTo", "smtpSubjectPrefix", "smtpTimeout" };

    public static AppSettings Load(ConfigLoadMode mode = ConfigLoadMode.Repair, string? configPath = null)
    {
        string file = configPath ?? AppPaths.ConfigPath;
        var settings = new AppSettings();
        if (File.Exists(file))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 读取 settings.json 失败：{ex.Message}");
                throw;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                ValidateCurrentFormat(document.RootElement);
                PluginRepositorySettingsValidator.ReadChannel(document.RootElement);
                AppSettings? parsed = JsonSerializer.Deserialize<AppSettings>(text, JsonOpts.Default);
                if (parsed is not null)
                {
                    PluginRepositorySettingsValidator.Normalize(parsed.PluginRepository);
                    settings = parsed;
                }
            }
            catch (PluginRepositoryConfigurationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                if (mode == ConfigLoadMode.Repair)
                {
                    string backup = JsonStore.PreserveCorruptFile(file);
                    if (string.IsNullOrWhiteSpace(backup))
                    {
                        throw new IOException("解析 settings.json 失败且无法保留原文件，已停止加载以保护原始数据", ex);
                    }
                    Logger.Warn($"[警告] 解析 settings.json 失败，使用默认设置：{ex.Message}，原文件已保留为 {Path.GetFileName(backup)}（可手动恢复，不再被后续保存覆盖）");
                }
                else
                {
                    Logger.Warn($"[警告] 解析 settings.json 失败，使用默认设置：{ex.Message}（只读启动不修改原文件）");
                }
            }
        }
        Normalize(settings);
        LocaleCatalog.SetHostLocale(settings.HostLocale);
        Logger.ConfigureLevel(settings.LogLevel);
        return settings;
    }

    public static void Save(AppSettings settings, string? configPath = null)
    {
        string file = configPath ?? AppPaths.ConfigPath;
        JsonObject? original = null;
        if (File.Exists(file))
        {
            original = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            ValidateCurrentFormat(document.RootElement);
        }
        Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var output = JsonSerializer.SerializeToNode(settings, JsonOpts.Default)!.AsObject();
        // A blocked notification migration must retain its source fields when unrelated settings are saved.
        if (original is not null)
            foreach (var pair in original.Where(pair => LegacyNotificationProperties.Contains(pair.Key)))
                output[pair.Key] = pair.Value?.DeepClone();
        JsonUtil.WriteAtomic(file, output.ToJsonString(JsonOpts.Indented));
        LocaleCatalog.SetHostLocale(settings.HostLocale);
        // 原子保存成功后才更新日志阈值，失败时保留旧阈值。
        Logger.ConfigureLevel(settings.LogLevel);
    }

    private static void ValidateCurrentFormat(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("unsupported_settings_format");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!names.Add(property.Name) || (!CurrentProperties.Contains(property.Name) && !LegacyNotificationProperties.Contains(property.Name)))
                throw new InvalidDataException("unsupported_settings_format");
        }
    }

    private static void Normalize(AppSettings settings)
    {
        if (!settings.UpdateCheckEnabled)
        {
            settings.UpdateAutoApplyEnabled = false;
        }
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
        settings.HostLocale = LocaleCatalog.Normalize(settings.HostLocale);
        settings.PluginPreferences ??= new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase);
        PluginRepositorySettingsValidator.Normalize(settings.PluginRepository);
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
