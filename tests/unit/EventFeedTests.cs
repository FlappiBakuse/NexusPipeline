using System.Text.Json;
using NexusPipeline.Host.Control.Events;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class EventFeedTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void SharesIdleSnapshots(int clients)
    {
        long mutation = 0, recovery = 0;
        var captures = 0;
        var feed = new ControlEventFeed(Guid.NewGuid(), () => new(mutation, recovery), () =>
        { captures++; return new { mutation, recovery }; });
        var first = JsonSerializer.SerializeToElement(feed.Snapshot());
        var stream = first.GetProperty("streamId").GetGuid();
        var sequence = first.GetProperty("sequence").GetInt64();
        for (var tick = 0; tick < 10; tick++)
            for (var client = 0; client < clients; client++) Assert.Empty(feed.Read(stream, sequence));
        Assert.Equal(1, captures);
        mutation++;
        var changed = Assert.Single(feed.Read(stream, sequence));
        Assert.Equal(sequence + 1, changed.Sequence);
        for (var client = 0; client < clients; client++) Assert.Equal(changed, Assert.Single(feed.Read(stream, sequence)));
        Assert.Equal(2, captures);
        sequence = changed.Sequence;
        recovery++;
        Assert.Equal(sequence + 1, Assert.Single(feed.Read(stream, sequence)).Sequence);
        Assert.Equal(3, captures);
        long progress = 0;
        var persistentReads = 0;
        var statistics = new ControlEventStatistics(() => mutation, () =>
        { persistentReads++; return new(mutation, mutation, mutation, mutation, mutation); });
        var progressFeed = new ControlEventFeed(Guid.NewGuid(), () => new(mutation, recovery, progress),
            () => new { statistics = statistics.Read(), progress });
        _ = progressFeed.Snapshot();
        for (var tick = 0; tick < 100; tick++) { progress++; _ = progressFeed.Snapshot(); }
        Assert.Equal(1, persistentReads);
        mutation++; _ = progressFeed.Snapshot(); Assert.Equal(2, persistentReads);
        recovery++; _ = progressFeed.Snapshot(); Assert.Equal(2, persistentReads);
        Console.WriteLine($"clients={clients}; idleTicks=10; idleCaptures=1; changedCaptures=2");
    }

    [Fact]
    public void PreservesStreamWatermarks()
    {
        long mutation = 0;
        var raced = false;
        var feed = new ControlEventFeed(Guid.NewGuid(), () => new(mutation, 0), () =>
        {
            var value = mutation;
            if (!raced) { mutation++; raced = true; }
            return new { mutation = value };
        });
        var first = JsonSerializer.SerializeToElement(feed.Snapshot());
        var stream = first.GetProperty("streamId").GetGuid();
        Assert.Equal(1, first.GetProperty("state").GetProperty("mutation").GetInt64());
        Assert.Empty(feed.Read(stream, 1));
        Assert.Equal("resync", Assert.Single(feed.Read(Guid.NewGuid(), 1)).Kind);
        for (var index = 0; index < 257; index++) { mutation++; feed.Snapshot(); }
        Assert.Equal("resync", Assert.Single(feed.Read(stream, 1)).Kind);
        var latest = JsonSerializer.SerializeToElement(feed.Snapshot()).GetProperty("sequence").GetInt64();
        Assert.Equal("resync", Assert.Single(feed.Read(stream, latest + 1)).Kind);
        Assert.Empty(feed.Read(stream, latest));
        var changing = false;
        var busy = new ControlEventFeed(Guid.NewGuid(), () => new(mutation, 0), () =>
        {
            var value = mutation;
            if (changing) mutation++;
            return new { mutation = value };
        });
        var stable = JsonSerializer.SerializeToElement(busy.Snapshot());
        var busyStream = stable.GetProperty("streamId").GetGuid();
        changing = true; mutation++;
        var resync = Assert.Single(busy.Read(busyStream, 1));
        Assert.Equal("resync", resync.Kind);
        Assert.Equal(1, resync.Sequence);
        Assert.Throws<IOException>(() => busy.Snapshot());
        changing = false;
        var settled = Assert.Single(busy.Read(busyStream, 1));
        Assert.Equal("state", settled.Kind);
        Assert.Equal(2, settled.Sequence);
        Assert.Contains("\"mutation\":" + mutation, settled.Frame);
    }
}
