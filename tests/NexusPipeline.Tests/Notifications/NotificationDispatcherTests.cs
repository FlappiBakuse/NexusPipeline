using NexusPipeline.Plugin.Abstractions;
using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;

namespace NexusPipeline.Tests.Notifications;

public sealed class NotificationDispatcherTests
{
    [Fact]
    public async Task TaskReportNotificationRetainsAccountContextRecipientAndScreenshot()
    {
        var settings = new AppSettings { SmtpEnabled = true, SmtpHost = "smtp.example.test", SmtpUser = "fixture", SmtpPassword = "fixture" };
        var image = new NotificationImage("fixture", "capture.png", "image/png", [1, 2, 3], 1, 1, DateTimeOffset.UtcNow);
        string? body = null, recipient = null; NotificationImage? captured = null;
        var dispatcher = new NotificationDispatcher(new TestSettingsProvider(settings), send: (_, message, smtp, _, screenshot) =>
        { body = message; recipient = smtp; captured = screenshot; return Task.FromResult(true); });
        var record = new NexusPipeline.Modules.History.RunRecord
        {
            UserName = "Fixture account", Attempts = 2, Status = "partial",
            TaskReport = System.Text.Json.Nodes.JsonNode.Parse("""{"originalPlan":{"tasks":[]},"finalTaskResults":[],"summary":{"counts":{"total":0}}}""")!.AsObject()
        };
        await dispatcher.NotifyScriptAsync(new NexusPipeline.Modules.Scripts.ScriptInstance { Name = "Fixture script" }, record,
            new NexusPipeline.Modules.Users.UserScriptBinding { SmtpTo = "  account@example.test  " }, image);
        Assert.Contains("Fixture account", body); Assert.Contains("Fixture script", body); Assert.Contains(record.Id, body);
        Assert.Equal("account@example.test", recipient); Assert.Same(image, captured);
    }

    [Fact]
    public async Task PluginNotificationUsesHostOwnedDispatcher()
    {
        var dispatcher = new NotificationDispatcher(
            new TestSettingsProvider(),
            TimeSpan.FromMilliseconds(50));
        Task send = dispatcher.SendPluginAsync(
            new PluginNotification("测试", "正文"),
            CancellationToken.None).AsTask();

        Task completed = await Task.WhenAny(send, Task.Delay(TimeSpan.FromMilliseconds(150)));

        Assert.Same(send, completed);
    }

    [Fact]
    public async Task PluginNotificationPassesTrimmedSmtpRecipientOverride()
    {
        var settings = new AppSettings
        {
            SmtpEnabled = true,
            SmtpHost = "smtp.example.test",
            SmtpUser = "sender@example.test",
            SmtpPassword = "password",
        };
        string? capturedRecipient = null;
        var dispatcher = new NotificationDispatcher(
            new TestSettingsProvider(settings),
            send: (_, _, smtpTo, _, _) =>
            {
                capturedRecipient = smtpTo;
                return Task.FromResult(true);
            });

        await dispatcher.SendPluginAsync(
            new PluginNotification("签到完成", "结果")
            {
                SmtpTo = "  task@example.test  ",
            },
            CancellationToken.None);

        Assert.Equal("task@example.test", capturedRecipient);
    }

    [Fact]
    public async Task PluginNotificationBlankSmtpOverrideUsesGlobalRecipient()
    {
        var settings = new AppSettings
        {
            SmtpEnabled = true,
            SmtpHost = "smtp.example.test",
            SmtpUser = "sender@example.test",
            SmtpPassword = "password",
            SmtpTo = "global@example.test",
        };
        bool sent = false;
        string? capturedRecipient = "unset";
        var dispatcher = new NotificationDispatcher(
            new TestSettingsProvider(settings),
            send: (_, _, smtpTo, _, _) =>
            {
                sent = true;
                capturedRecipient = smtpTo;
                return Task.FromResult(true);
            });

        await dispatcher.SendPluginAsync(
            new PluginNotification("签到完成", "结果")
            {
                SmtpTo = "   ",
            },
            CancellationToken.None);

        Assert.True(sent);
        Assert.Null(capturedRecipient);
    }

    private sealed class TestSettingsProvider : ISettingsProvider
    {
        public TestSettingsProvider(AppSettings? current = null) => Current = current ?? new AppSettings();

        public AppSettings Current { get; }
    }
}
