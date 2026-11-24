using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Host.Control.Events;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ControlEventFeedTests
{
    [Fact(DisplayName = "A Published Revision Encodes Its Snapshot Once And The Size Limit Does Not Include The Sse Envelope")]
    public void ReusesPublishedState()
    {
        var payloadLength = 4096 - "{\"label\":\"\"}"u8.Length;
        var first = new Snapshot(new string('x', payloadLength));
        var current = first;
        var revision = 1L;
        var instance = Guid.NewGuid();
        var feed = new ControlEventFeed(instance, () => new(revision, 0), () => current);

        var snapshot = JsonSerializer.SerializeToElement(feed.Snapshot(), JsonSerializerOptions.Web);
        var stream = snapshot.GetProperty("streamId").GetGuid();
        var sequence = snapshot.GetProperty("sequence").GetInt64();
        var state = snapshot.GetProperty("state");
        Assert.Equal(4096, System.Text.Encoding.UTF8.GetByteCount(state.GetRawText()));
        var message = Assert.Single(feed.Read(stream, sequence - 1));
        Assert.Equal(sequence, message.Sequence);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(message.Frame) > 4096);
        using (var frame = JsonDocument.Parse(message.Frame.Split('\n')[1][6..]))
        {
            Assert.Equal(state.GetRawText(), frame.RootElement.GetProperty("data").GetRawText());
            Assert.Equal(instance, frame.RootElement.GetProperty("instanceId").GetGuid());
            Assert.Equal("state", frame.RootElement.GetProperty("kind").GetString());
        }
        Assert.Empty(feed.Read(stream, sequence));
        Assert.Equal(state.GetRawText(), JsonSerializer.SerializeToElement(feed.Snapshot(), JsonSerializerOptions.Web).GetProperty("state").GetRawText());
        Assert.Equal(1, first.Reads);

        revision++; current = new(new string('x', payloadLength + 1));
        Assert.Equal("control_event_snapshot_limit", Assert.Throws<IOException>(() => feed.Snapshot()).Message);
        revision++; current = new("second\n值");
        var next = Assert.Single(feed.Read(stream, sequence));
        Assert.Equal(sequence + 1, next.Sequence);
        using var nextFrame = JsonDocument.Parse(next.Frame.Split('\n')[1][6..]);
        Assert.Equal("second\n值", nextFrame.RootElement.GetProperty("data").GetProperty("label").GetString());
        Assert.Equal(1, current.Reads);
    }

    private sealed class Snapshot(string label)
    {
        internal int Reads { get; private set; }
        [JsonPropertyName("label")]
        public string Label { get { Reads++; return label; } }
    }
}
