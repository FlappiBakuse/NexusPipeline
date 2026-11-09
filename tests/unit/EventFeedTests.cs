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
    public void SharesCurrentSnapshots(int clients)
    {
        long mutation = 0, recovery = 0;
        var feed = new ControlEventFeed(Guid.NewGuid(), () => new(mutation, recovery), () =>
            new { mutation, recovery });
        var first = JsonSerializer.SerializeToElement(feed.Snapshot());
        var stream = first.GetProperty("streamId").GetGuid();
        var sequence = first.GetProperty("sequence").GetInt64();
        for (var tick = 0; tick < 10; tick++)
            for (var client = 0; client < clients; client++) Assert.Empty(feed.Read(stream, sequence));
        mutation++;
        var changed = Assert.Single(feed.Read(stream, sequence));
        Assert.Equal(sequence + 1, changed.Sequence);
        for (var client = 0; client < clients; client++) Assert.Equal(changed, Assert.Single(feed.Read(stream, sequence)));
        sequence = changed.Sequence;
        recovery++;
        Assert.Equal(sequence + 1, Assert.Single(feed.Read(stream, sequence)).Sequence);
        long progress = 0;
        var statistics = new ControlEventStatistics(() => mutation, () =>
            new(mutation, mutation, mutation, mutation, mutation));
        var progressFeed = new ControlEventFeed(Guid.NewGuid(), () => new(mutation, recovery, progress),
            () => new { statistics = statistics.Read(), progress });
        AssertProgress();
        for (var tick = 0; tick < 100; tick++) { progress++; AssertProgress(); }
        mutation++; AssertProgress();
        recovery++; AssertProgress();
        void AssertProgress()
        {
            var state = JsonSerializer.SerializeToElement(progressFeed.Snapshot()).GetProperty("state");
            Assert.Equal(progress, state.GetProperty("progress").GetInt64());
            Assert.Equal(new ControlPersistentState(mutation, mutation, mutation, mutation, mutation),
                state.GetProperty("statistics").Deserialize<ControlPersistentState>());
        }
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
