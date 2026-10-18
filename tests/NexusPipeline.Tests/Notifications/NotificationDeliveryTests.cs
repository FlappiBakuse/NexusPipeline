using System.Collections.Concurrent;
using NexusPipeline.Modules.Notifications;
using Xunit;

namespace NexusPipeline.Tests.Notifications;

public sealed class NotificationDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notification-delivery-" + Guid.NewGuid().ToString("N"));
    public NotificationDeliveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private NotificationTasks Store() => new(Path.Combine(_root, "notifications.json"), () => 99);
    private static string Add(NotificationTasks store, string name, bool enabled = true, int timeout = 30)
    {
        var input = new NotificationTaskInput { Name = name, Type = "webhook", Enabled = enabled,
            Configuration = new() { Timeout = timeout }, Secrets = new() { ["webhookUrl"] = new("replace", "https://example.invalid/hook") } };
        var result = store.Save(null, input);
        Assert.True(result.Succeeded, result.ErrorCode);
        return result.Value!.Id;
    }

    [Fact]
    public async Task TargetsAreDeduplicatedAndUnavailableTasksSkipped()
    {
        var store = Store(); string first = Add(store, "first"); string second = Add(store, "second");
        string disabled = Add(store, "disabled", false); string deleted = Add(store, "deleted");
        Assert.True(store.Delete(deleted).Succeeded);
        var calls = new ConcurrentBag<string>();
        var delivery = new NotificationDelivery(store, send: (task, _, _, _) => { calls.Add(task.Id); return Task.FromResult(true); });
        await delivery.SendAsync([], "empty");
        Assert.Empty(calls);
        await delivery.SendAsync([first, second, first, disabled, deleted], "event");
        Assert.Equal(new[] { first, second }.Order(), calls.Order());
        Assert.True((await delivery.TestAsync(disabled)).Succeeded);
        Assert.False(store.Find(disabled)!.Enabled);
    }

    [Fact]
    public async Task BoundedConcurrencyAndOneTargetFailureDoNotStopOthers()
    {
        var store = Store(); var ids = Enumerable.Range(0, 8).Select(i => Add(store, i.ToString())).ToArray();
        int active = 0, maximum = 0, completed = 0;
        var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new NotificationDelivery(store, send: async (task, _, _, token) => {
            int current = Interlocked.Increment(ref active);
            int seen;
            do { seen = maximum; } while (current > seen && Interlocked.CompareExchange(ref maximum, current, seen) != seen);
            if (current == 4) firstBatch.TrySetResult();
            try {
                await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                if (task.Id == ids[0]) throw new IOException("synthetic secret must not be logged");
                Interlocked.Increment(ref completed); return true;
            } finally { Interlocked.Decrement(ref active); }
        });
        await delivery.SendAsync(ids, "event");
        Assert.Equal(4, maximum);
        Assert.Equal(7, completed);
    }

    [Fact]
    public async Task TimeoutCancelsTransportAndOtherTargetCompletes()
    {
        var store = Store(); string timed = Add(store, "timed", timeout: 1); string other = Add(store, "other");
        bool cancelled = false, completed = false;
        var delivery = new NotificationDelivery(store, send: async (task, _, _, token) => {
            if (task.Id == other) { completed = true; return true; }
            try { await Task.Delay(Timeout.Infinite, token); return true; }
            catch (OperationCanceledException) { cancelled = true; throw; }
        });
        await delivery.SendAsync([timed, other], "event");
        Assert.True(cancelled); Assert.True(completed);
    }
}
