using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Services.Notification;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class NotificationDispatcherTests
{
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
