using NexusPipeline.Core.Records;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class LiveLogTests
{
    private static RunLogKey Key => new(Guid.NewGuid(), Guid.NewGuid(), "main");

    [Fact(DisplayName = "Live Tail Keeps Only Bounded Bytes And Frames With Original Offsets And File Epochs")]
    public void BoundsTailStorage()
    {
        var feed = new LiveLogFeed(Key);
        for (var index = 0; index < 80; index++) feed.Publish(index * 4096L, "file", new byte[4096], index / 40, index * 4096L);
        var tail = feed.ReadTail();
        Assert.True(tail.PrefixOmitted); Assert.Equal(65536, tail.Frames.Sum(frame => frame.Bytes.Length));
        Assert.Equal(80, tail.LastSequence); Assert.Equal(80 * 4096, tail.ObservedEnd);
        Assert.All(tail.Frames, frame => { Assert.Equal("file", frame.Source); Assert.Equal(1, frame.FileSourceId); });
        var last = Assert.Single(feed.ReadTail(17).Frames);
        Assert.Equal(17, last.Bytes.Length); Assert.Equal(80 * 4096 - 17, last.ObservedOffset);
        Assert.Equal(last.ObservedOffset, last.FileOffset); Assert.True(last.GapBefore);
    }

    [Fact(DisplayName = "A Slow Subscriber Disconnects With An Explicit Gap Without Blocking The Other Reader")]
    public void DisconnectsSlowReaders()
    {
        var feed = new LiveLogFeed(Key);
        using var slow = feed.Subscribe(); using var fast = feed.Subscribe();
        for (var index = 0; index < 100; index++)
        {
            feed.Publish(index, "stdout", [(byte)index]);
            Assert.True(fast.Events.TryRead(out var current)); Assert.Equal(index + 1, current.Sequence);
        }
        Assert.Equal("run_log_subscription_gap", slow.FailureCode);
        var queued = 0; while (slow.Events.TryRead(out _)) queued++;
        Assert.Equal(8, queued); Assert.Null(fast.FailureCode);
        Assert.Equal(100, feed.ReadTail().LastSequence);
    }

    [Fact(DisplayName = "Live Copies Cannot Mutate The Producer Tail Or Another Subscriber And Offsets Cannot Regress")]
    public void OwnsSubscriberBytes()
    {
        var feed = new LiveLogFeed(Key);
        using var first = feed.Subscribe(); using var second = feed.Subscribe();
        var bytes = new byte[] { 1, 2, 3 }; feed.Publish(4, "stderr", bytes); bytes[0] = 9;
        Assert.True(first.Events.TryRead(out var one)); Assert.True(one.GapBefore); one.Bytes[0] = 8;
        Assert.True(second.Events.TryRead(out var two)); Assert.Equal(1, two.Bytes[0]);
        var tail = feed.ReadTail(); tail.Frames[0].Bytes[0] = 7;
        Assert.Equal(1, feed.ReadTail().Frames[0].Bytes[0]);
        Assert.Throws<ArgumentException>(() => feed.Publish(5, "stdout", [1]));
        Assert.Throws<ArgumentException>(() => feed.Publish(7, "stdout", [1], 1));
    }

    [Fact(DisplayName = "Subscription Limits And Completion Release Each Admission Exactly Once")]
    public void ReleasesSubscriptionClaims()
    {
        var feed = new LiveLogFeed(Key); var released = 0;
        var subscriptions = Enumerable.Range(0, 8).Select(_ => feed.Subscribe(() => released++)).ToArray();
        Assert.Throws<IOException>(() => feed.Subscribe());
        subscriptions[0].Dispose(); subscriptions[0].Dispose(); Assert.Equal(1, released);
        using var replacement = feed.Subscribe(() => released++);
        feed.Complete(true); feed.Complete(false, "ignored");
        Assert.True(feed.ReadTail().IsComplete); Assert.Equal(9, released);
        foreach (var item in subscriptions) item.Dispose(); replacement.Dispose(); Assert.Equal(9, released);
        Assert.Throws<IOException>(() => feed.Subscribe());
    }
}
