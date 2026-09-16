using NexusPipeline.Services;
using NexusPipeline.Services.Realtime;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class RealtimeEventBusTests
{
    [Fact]
    public async Task SubscriberOverflowEmitsMissedBeforeRemainingEvents()
    {
        var bus = new RealtimeEventBus();
        using RealtimeEventSubscription subscription = bus.Subscribe(capacity: 2);

        bus.Publish("test.one", new { value = 1 });
        bus.Publish("test.two", new { value = 2 });
        bus.Publish("test.three", new { value = 3 });

        RealtimeEventDelivery? missed = await subscription.ReadAsync(CancellationToken.None);
        Assert.True(missed.HasValue);
        Assert.True(missed.Value.Missed);

        RealtimeEventDelivery? first = await subscription.ReadAsync(CancellationToken.None);
        RealtimeEventDelivery? second = await subscription.ReadAsync(CancellationToken.None);
        Assert.Equal("test.two", first?.Event?.Type);
        Assert.Equal("test.three", second?.Event?.Type);
    }

    [Fact]
    public async Task LogEntriesAreBatchedIntoOneRunLogEvent()
    {
        var bus = new RealtimeEventBus();
        using RealtimeEventSubscription subscription = bus.Subscribe();
        var first = new ExecutionLogEntry(1, DateTimeOffset.UtcNow, LogLevel.Info, "one", "one");
        var second = new ExecutionLogEntry(2, DateTimeOffset.UtcNow, LogLevel.Warn, "two", "two");

        bus.PublishLog("run-1", first);
        bus.PublishLog("run-1", second);
        bus.FlushPendingLogs();

        RealtimeEventDelivery? delivery = await subscription.ReadAsync(CancellationToken.None);
        Assert.Equal("run.log", delivery?.Event?.Type);
        Assert.Equal(2, delivery?.Event?.Envelope.Data.GetProperty("entries").GetArrayLength());
        Assert.Equal("run-1", delivery?.Event?.Envelope.Data.GetProperty("runId").GetString());
    }

    [Fact]
    public void RunningStatusProjectionDoesNotIncludeLogPayload()
    {
        var snapshot = new RunningExecutionStatusSnapshot
        {
            Id = "run-1",
            Kind = "script",
            TargetId = "script-1",
            TargetName = "Demo",
            Status = "running",
            TotalTasks = 1,
            CurrentStatus = "执行中",
        };

        string json = System.Text.Json.JsonSerializer.Serialize(
            RealtimeEventProjection.RunStatus(snapshot, active: true),
            NexusPipeline.Utilities.JsonOpts.Web);

        Assert.Contains("runId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("logEntries", json, StringComparison.Ordinal);
    }
}
