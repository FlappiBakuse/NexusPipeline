using System.Text.Json.Nodes;
using NexusPipeline.Modules.Settings.Persistence;
using Xunit;

namespace NexusPipeline.Tests.Settings;

public sealed class SettingsMigrationTests
{
    [Fact]
    public void RetiredDingTalkReadOnlyDisablesSendingWithoutChangingOriginalBytes()
    {
        string root = NewRoot();
        try
        {
            string file = Path.Combine(root, "settings.json");
            string original = """
                {"webhookEnabled":true,"webhookType":" DingTalk ","webhookUrl":"https://old.example","webhookSecret":"old-secret","webhookTemplate":"old-template","dingTalkAppSecret":"retired","smtpEnabled":true,"autoOpenBrowser":false}
                """;
            File.WriteAllText(file, original);

            var settings = AppSettingsStore.Load(ConfigLoadMode.ReadOnly, file);
            Assert.False(settings.WebhookEnabled);
            Assert.Equal("feishu", settings.WebhookType);
            Assert.Equal("", settings.WebhookUrl);
            Assert.Equal("", settings.WebhookSecret);
            Assert.Equal("", settings.WebhookTemplate);
            Assert.True(settings.SmtpEnabled);
            Assert.False(settings.AutoOpenBrowser);
            Assert.Equal(original, File.ReadAllText(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RepairRemovesRetiredFieldsButKeepsOtherWebhookAndIsIdempotent()
    {
        string root = NewRoot();
        try
        {
            string file = Path.Combine(root, "settings.json");
            File.WriteAllText(file, """
                {"webhookEnabled":true,"webhookType":"slack","webhookUrl":"https://hooks.slack.com/fixture","webhookSecret":"keep","dingTalkRobotCode":"retired"}
                """);
            var settings = AppSettingsStore.Load(ConfigLoadMode.Repair, file);
            Assert.True(settings.WebhookEnabled);
            Assert.Equal("slack", settings.WebhookType);
            Assert.Equal("https://hooks.slack.com/fixture", settings.WebhookUrl);
            Assert.Equal("keep", settings.WebhookSecret);
            string migrated = File.ReadAllText(file);
            Assert.Null(JsonNode.Parse(migrated)?["dingTalkRobotCode"]);
            AppSettingsStore.Load(ConfigLoadMode.Repair, file);
            Assert.Equal(migrated, File.ReadAllText(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void BrowserDefaultAppliesOnlyWhenFieldIsAbsent()
    {
        string root = NewRoot();
        try
        {
            string file = Path.Combine(root, "settings.json");
            File.WriteAllText(file, "{}");
            Assert.True(AppSettingsStore.Load(ConfigLoadMode.ReadOnly, file).AutoOpenBrowser);
            File.WriteAllText(file, "{\"autoOpenBrowser\":false}");
            Assert.False(AppSettingsStore.Load(ConfigLoadMode.ReadOnly, file).AutoOpenBrowser);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-settings-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
