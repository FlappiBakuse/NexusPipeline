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

    private sealed class TestSettingsProvider : ISettingsProvider
    {
        public AppSettings Current { get; } = new();
    }
}
