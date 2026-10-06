using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Persistence;
using Xunit;

namespace NexusPipeline.Tests.Settings;

public sealed class SettingsFormatTests
{
    [Fact]
    public void UnsupportedNotificationInputsAreRejectedWithoutChangingOriginalFiles()
    {
        string root = NewRoot();
        try
        {
            string file = Path.Combine(root, "settings.json");
            foreach (string original in new[]
            {
                "{\"webhookEnabled\":true,\"webhookType\":\"dingtalk\"}",
                "{\"webhookEnabled\":true,\"webhookType\":\" DingTalk \"}",
                "{\"webhookEnabled\":true,\"webhookType\":\"unknown\"}",
                "{\"webhookType\":null}",
                "{\"webhookType\":\"slack\",\"dingTalkAppKey\":\"retired\"}",
                "{\"webhookType\":\"slack\",\"DINGTALKAPPSECRET\":\"retired\"}",
                "{\"webhookType\":\"slack\",\"dingTalkRobotCode\":\"retired\"}",
                "{\"dingTalkOpenConversationId\":null}",
                "{\"webhookType\":\"slack\",\"WebhookType\":\"feishu\"}",
                "{\"unrecognizedNotificationSetting\":true}"
            })
            {
                File.WriteAllText(file, original);
                foreach (ConfigLoadMode mode in new[] { ConfigLoadMode.ReadOnly, ConfigLoadMode.Repair })
                {
                    Assert.Equal("unsupported_settings_format", Assert.Throws<InvalidDataException>(() => AppSettingsStore.Load(mode, file)).Message);
                    Assert.Equal(original, File.ReadAllText(file));
                    Assert.Single(Directory.GetFiles(root));
                }
                Assert.Throws<InvalidDataException>(() => AppSettingsStore.Save(new AppSettings(), file));
                Assert.Equal(original, File.ReadAllText(file));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CurrentChannelsRoundTripWithoutRewritingDuringLoad()
    {
        string root = NewRoot();
        try
        {
            string file = Path.Combine(root, "settings.json");
            foreach (string channel in AppSettings.WebhookTypes)
            {
                string original = "{\"webhookEnabled\":true,\"webhookType\":\"" + channel + "\",\"webhookUrl\":\"https://example.test\",\"openDesktopOnStartup\":false}";
                File.WriteAllText(file, original);
                var settings = AppSettingsStore.Load(ConfigLoadMode.Repair, file);
                Assert.Equal(channel, settings.WebhookType);
                Assert.True(settings.WebhookEnabled);
                Assert.False(settings.OpenDesktopOnStartup);
                Assert.Equal(original, File.ReadAllText(file));
                AppSettingsStore.Save(settings, file);
                Assert.Equal(channel, AppSettingsStore.Load(ConfigLoadMode.ReadOnly, file).WebhookType);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DesktopStartupDefaultAppliesOnlyWhenFieldIsAbsent()
    {
        string root = NewRoot();
        try
        {
            string file = Path.Combine(root, "settings.json");
            File.WriteAllText(file, "{}");
            Assert.False(AppSettingsStore.Load(ConfigLoadMode.ReadOnly, file).OpenDesktopOnStartup);
            File.WriteAllText(file, "{\"openDesktopOnStartup\":true}");
            Assert.True(AppSettingsStore.Load(ConfigLoadMode.ReadOnly, file).OpenDesktopOnStartup);
            File.WriteAllText(file, "{\"autoOpenBrowser\":true}");
            Assert.Throws<InvalidDataException>(() => AppSettingsStore.Load(ConfigLoadMode.ReadOnly, file));
            Assert.Equal("{\"autoOpenBrowser\":true}", File.ReadAllText(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-settings-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
