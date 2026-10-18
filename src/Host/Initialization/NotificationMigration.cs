using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Serialization;

using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Settings.Persistence;

namespace NexusPipeline.Host.Initialization;

internal sealed record NotificationMigrationFile(string Path, byte[]? Before, byte[] After);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record NotificationMigrationEntry(string RelativePath, string? Before, string After, string? BeforeHash, string AfterHash);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class NotificationMigrationJournal
{
    [JsonRequired]
    public int SchemaVersion { get; set; } = 1;
    public bool Committed { get; set; }
    public List<NotificationMigrationEntry> Files { get; set; } = [];
}

internal static class NotificationMigration
{
    internal static string? Run(string configRoot, string journalPath,
        Func<Func<string, IReadOnlyList<string>>, IReadOnlyList<NotificationMigrationFile>> preparePlugins,
        Action<int>? afterWrite = null)
    {
        try
        {
            configRoot = System.IO.Path.GetFullPath(configRoot);
            string notificationsPath = System.IO.Path.Combine(configRoot, "notifications.json");
            if (File.Exists(journalPath))
            {
                var journal = JsonSerializer.Deserialize<NotificationMigrationJournal>(File.ReadAllText(journalPath), JsonOpts.Default)
                    ?? throw new InvalidDataException();
                ValidateJournal(configRoot, journal);
                if (!journal.Committed) Commit(configRoot, journalPath, journal, afterWrite);
                else if (!File.Exists(notificationsPath)) throw new InvalidDataException();
                return null;
            }
            if (File.Exists(notificationsPath)) return null;
            var files = new List<NotificationMigrationFile>();
            string settingsPath = System.IO.Path.Combine(configRoot, "settings.json");
            byte[]? settingsBytes = Read(settingsPath);
            JsonObject settings = settingsBytes is null ? new() : ParseObject(settingsBytes);
            foreach (var group in settings.Select(pair => pair.Key).GroupBy(key => key, StringComparer.OrdinalIgnoreCase))
                if (group.Count() != 1) throw new InvalidDataException();
            foreach (string key in settings.Select(pair => pair.Key).Where(IsLegacyField).ToArray())
            {
                string canonical = AppSettingsStore.LegacyNotificationProperties.FirstOrDefault(name => name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException();
                if (canonical == key) continue;
                JsonNode? value = settings[key]; settings.Remove(key); settings[canonical] = value;
            }
            var configuration = JsonSerializer.Deserialize<NotificationChannelSettings>(settings.ToJsonString(), JsonOpts.Default) ?? throw new InvalidDataException();
            if (!configuration.HasNonNullFields() || !NotificationChannelSettings.WebhookTypes.Contains(configuration.WebhookType))
                throw new InvalidDataException();
            var tasks = new List<NotificationTask>();
            var smtpTargets = new Dictionary<string, string>(StringComparer.Ordinal);
            string? webhookId = null;
            bool hasWebhook = new[] { configuration.WebhookUrl, configuration.WebhookSecret, configuration.FeishuAppId, configuration.FeishuAppSecret, configuration.SlackBotToken, configuration.SlackChannelId, configuration.WebhookTemplate }.Any(value => !string.IsNullOrWhiteSpace(value));
            bool hasSmtp = new[] { configuration.SmtpHost, configuration.SmtpUser, configuration.SmtpPassword, configuration.SmtpFrom, configuration.SmtpTo }.Any(value => !string.IsNullOrWhiteSpace(value));
            if (hasWebhook)
            {
                var task = Create("Webhook", "webhook", configuration, settings["webhookEnabled"]?.GetValue<bool>() ?? true,
                    settings["webhookScreenshotEnabled"]?.GetValue<bool>() ?? false, settings["webhookTimeout"]?.GetValue<int>() ?? 30);
                tasks.Add(task); webhookId = task.Id;
            }
            IReadOnlyList<string> Resolve(string recipient)
            {
                var result = new List<string>();
                if (webhookId is not null) result.Add(webhookId);
                string to = string.IsNullOrWhiteSpace(recipient) ? configuration.SmtpTo.Trim() : recipient.Trim();
                if (hasSmtp || to.Length != 0)
                {
                    if (!smtpTargets.TryGetValue(to, out string? id))
                    {
                        var task = Create(smtpTargets.Count == 0 ? "SMTP" : $"SMTP {smtpTargets.Count + 1}", "smtp", configuration,
                            settings["smtpEnabled"]?.GetValue<bool>() ?? false, settings["smtpScreenshotEnabled"]?.GetValue<bool>() ?? false,
                            settings["smtpTimeout"]?.GetValue<int>() ?? 30);
                        task.Configuration.SmtpTo = to;
                        tasks.Add(task); smtpTargets[to] = id = task.Id;
                    }
                    result.Add(id);
                }
                if (tasks.Count > 99) throw new NotificationMigrationException("notification_migration_limit_exceeded");
                return result;
            }
            Resolve("");
            foreach (string name in new[] { "users.json", "queues.json" })
            {
                string path = System.IO.Path.Combine(configRoot, name);
                byte[]? original = Read(path);
                if (original is null) continue;
                var list = JsonNode.Parse(Decode(original)) as JsonArray ?? throw new InvalidDataException();
                foreach (JsonObject entity in list.Select(node => node as JsonObject ?? throw new InvalidDataException()))
                {
                    if (name == "queues.json") ReplaceTargets(entity, Resolve);
                    else
                    {
                        JsonNode? rawBindings = Get(entity, "bindings");
                        if (rawBindings is not null && rawBindings is not JsonArray) throw new InvalidDataException();
                        if (rawBindings is JsonArray bindings)
                            foreach (JsonObject binding in bindings.Select(node => node as JsonObject ?? throw new InvalidDataException())) ReplaceTargets(binding, Resolve);
                        JsonNode? rawOverrides = Get(entity, "bindingOverrides");
                        if (rawOverrides is not null && rawOverrides is not JsonObject) throw new InvalidDataException();
                        if (rawOverrides is JsonObject overrides)
                        {
                            JsonNode? rawNotification = Get(overrides, "notification");
                            if (rawNotification is not null && rawNotification is not JsonObject) throw new InvalidDataException();
                            if (rawNotification is JsonObject notification) ReplaceTargets(notification, Resolve);
                        }
                    }
                }
                files.Add(new(path, original, Encode(list)));
            }
            files.AddRange(preparePlugins(Resolve).Select(file => file with { After = EncodeText(Decode(file.After)) }));
            foreach (string key in settings.Select(pair => pair.Key).Where(IsLegacyField).ToArray()) settings.Remove(key);
            if (settingsBytes is not null) files.Add(new(settingsPath, settingsBytes, Encode(settings)));
            files.Add(new(notificationsPath, null, EncodeText(JsonSerializer.Serialize(new NotificationTaskStore { Tasks = tasks }, JsonOpts.Indented))));
            var prepared = new NotificationMigrationJournal { Files = files.Select(file => new NotificationMigrationEntry(
                System.IO.Path.GetRelativePath(configRoot, file.Path), file.Before is null ? null : Convert.ToBase64String(file.Before),
                Convert.ToBase64String(file.After), file.Before is null ? null : Hash(file.Before), Hash(file.After))).ToList() };
            ValidateJournal(configRoot, prepared);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(journalPath)!);
            JsonUtil.WriteAtomic(journalPath, JsonSerializer.Serialize(prepared, JsonOpts.Indented));
            Commit(configRoot, journalPath, prepared, afterWrite);
            return null;
        }
        catch (NotificationMigrationException error) { return error.Message; }
        catch { return "notification_migration_blocked"; }
    }

    private static NotificationTask Create(string name, string type, NotificationChannelSettings source, bool enabled, bool screenshots, int timeout)
    {
        var config = type == "smtp"
            ? new NotificationChannelSettings { SmtpHost = source.SmtpHost, SmtpPort = source.SmtpPort,
                SmtpSecure = source.SmtpSecure, SmtpUser = source.SmtpUser, SmtpPassword = source.SmtpPassword,
                SmtpFrom = source.SmtpFrom, SmtpTo = source.SmtpTo, SmtpSubjectPrefix = source.SmtpSubjectPrefix }
            : new NotificationChannelSettings { WebhookType = source.WebhookType, WebhookUrl = source.WebhookUrl,
                WebhookSecret = source.WebhookSecret, WebhookTemplate = source.WebhookTemplate,
                FeishuAppId = source.FeishuAppId, FeishuAppSecret = source.FeishuAppSecret,
                SlackBotToken = source.SlackBotToken, SlackChannelId = source.SlackChannelId };
        config.Timeout = timeout; config.ScreenshotEnabled = screenshots;
        foreach (string key in NotificationChannelSettings.SecretNames)
        {
            string secret = config.GetSecret(key);
            if (secret.Length != 0 && !SecretStore.IsEncrypted(secret)) config.SetSecret(key, SecretStore.Encrypt(secret));
        }
        return new() { Name = name, Type = type, Enabled = enabled, Configuration = config };
    }

    private static JsonNode? Get(JsonObject node, string name)
    {
        var fields = node.Where(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (fields.Length > 1) throw new InvalidDataException();
        return fields.Length == 0 ? null : fields[0].Value;
    }

    private static void ReplaceTargets(JsonObject node, Func<string, IReadOnlyList<string>> resolve)
    {
        JsonNode? existing = Get(node, "notificationTaskIds");
        if (existing is JsonArray idsBefore && idsBefore.Count != 0
            || existing is not null && existing is not JsonArray)
            throw new InvalidDataException("notification_migration_mixed_schema");
        var ids = resolve(Get(node, "smtpTo")?.GetValue<string>() ?? "");
        foreach (string key in node.Select(pair => pair.Key).Where(key => key.Equals("smtpTo", StringComparison.OrdinalIgnoreCase)
            || key.Equals("notificationTaskIds", StringComparison.OrdinalIgnoreCase)).ToArray()) node.Remove(key);
        node["notificationTaskIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
    }

    internal static bool IsLegacyField(string key) => new[] { "smtp", "webhook", "feishu", "slack" }.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    private static byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    private static JsonObject ParseObject(byte[] bytes) => JsonNode.Parse(Decode(bytes)) as JsonObject ?? throw new InvalidDataException();
    private static byte[] Encode(JsonNode node) => EncodeText(node.ToJsonString(JsonOpts.Indented));
    private static byte[] EncodeText(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Target(string root, string relative)
    {
        if (System.IO.Path.IsPathRooted(relative)) throw new InvalidDataException();
        string[] segments = relative.Replace('\\', '/').Split('/');
        bool hostFile = segments.Length == 1 && segments[0] is "settings.json" or "users.json" or "queues.json" or "notifications.json";
        bool pluginFile = segments.Length >= 4 && segments[0] == "plugins" && segments[2] == "scopes"
            && segments.All(segment => segment.Length != 0 && segment is not ("." or "..")) && relative.EndsWith(".json", StringComparison.Ordinal);
        if (!hostFile && !pluginFile) throw new InvalidDataException();
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!full.StartsWith(root.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        PayloadPathSafety.RequireLinkFree(full);
        return full;
    }

    private static void ValidateJournal(string root, NotificationMigrationJournal journal)
    {
        if (journal.SchemaVersion != 1 || journal.Files is null || journal.Files.Count is < 1 or > 1024
            || journal.Files.Select(file => Target(root, file.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count)
            throw new InvalidDataException();
        if (journal.Files[^1].RelativePath != "notifications.json" || journal.Files[^1].Before is not null)
            throw new InvalidDataException();
        foreach (var file in journal.Files)
        {
            if (Hash(Convert.FromBase64String(file.After)) != file.AfterHash
                || (file.Before is null ? file.BeforeHash is not null : Hash(Convert.FromBase64String(file.Before)) != file.BeforeHash)) throw new InvalidDataException();
        }
    }

    private static void Commit(string root, string journalPath, NotificationMigrationJournal journal, Action<int>? afterWrite)
    {
        foreach (var file in journal.Files)
        {
            byte[]? current = Read(Target(root, file.RelativePath));
            string? hash = current is null ? null : Hash(current);
            if (hash != file.BeforeHash && hash != file.AfterHash) throw new InvalidDataException("notification_migration_source_changed");
        }
        for (int i = 0; i < journal.Files.Count; i++)
        {
            var file = journal.Files[i]; string path = Target(root, file.RelativePath);
            if (Read(path) is { } current && Hash(current) == file.AfterHash) continue;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            JsonUtil.WriteAtomic(path, Decode(Convert.FromBase64String(file.After)));
            afterWrite?.Invoke(i);
        }
        journal.Committed = true;
        JsonUtil.WriteAtomic(journalPath, JsonSerializer.Serialize(journal, JsonOpts.Indented));
    }
}

internal sealed class NotificationMigrationException(string code) : Exception(code);
