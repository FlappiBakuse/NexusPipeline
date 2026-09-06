using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class RecentScreenshotCacheTests
{
    [Fact]
    public async Task RefreshStoresFrameAndHonorsIntervalAndSingleFlight()
    {
        DateTimeOffset now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        int calls = 0;
        using var cache = new RecentScreenshotCache(
            _ =>
            {
                Interlocked.Increment(ref calls);
                return ExecutionPreviewImageResult.Success(new byte[] { 1, 2, 3 });
            },
            intervalMilliseconds: 1000,
            now: () => now);
        cache.BeginAttempt(1);

        Task first = cache.TryRefreshAsync(1, 42, CancellationToken.None);
        Task second = cache.TryRefreshAsync(1, 42, CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.True(cache.TryGet(1, out RecentScreenshotFrame frame, out TimeSpan age));
        Assert.Equal(42, frame.ProcessId);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Data);
        Assert.Equal(TimeSpan.Zero, age);

        await cache.TryRefreshAsync(1, 42, CancellationToken.None);
        Assert.Equal(1, calls);

        now = now.AddSeconds(1);
        await cache.TryRefreshAsync(1, 42, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExpiredFrameIsNotReturned()
    {
        DateTimeOffset now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        using var cache = new RecentScreenshotCache(
            _ => ExecutionPreviewImageResult.Success(new byte[] { 9 }),
            intervalMilliseconds: 1000,
            maxAge: TimeSpan.FromSeconds(2),
            now: () => now);
        cache.BeginAttempt(1);
        await cache.TryRefreshAsync(1, 42, CancellationToken.None);

        now = now.AddSeconds(2).AddMilliseconds(1);

        Assert.False(cache.TryGet(1, out _, out _));
    }

    [Fact]
    public async Task BeginAttemptDiscardsPreviousAttemptFrame()
    {
        using var cache = new RecentScreenshotCache(
            _ => ExecutionPreviewImageResult.Success(new byte[] { 7 }),
            now: () => new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        cache.BeginAttempt(1);
        await cache.TryRefreshAsync(1, 42, CancellationToken.None);

        cache.BeginAttempt(2);

        Assert.False(cache.TryGet(2, out _, out _));
        Assert.False(cache.TryGet(1, out _, out _));
    }

    [Fact]
    public void StoreAcceptsSuccessfulLiveFrameButRejectsInvalidInput()
    {
        DateTimeOffset now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        using var cache = new RecentScreenshotCache(
            _ => ExecutionPreviewImageResult.Failure("not used"),
            now: () => now);
        cache.BeginAttempt(1);

        cache.Store(1, 42, ExecutionPreviewImageResult.Success(new byte[] { 4, 5 }));
        Assert.True(cache.TryGet(1, out RecentScreenshotFrame frame, out _));
        Assert.Equal(42, frame.ProcessId);
        Assert.Equal(new byte[] { 4, 5 }, frame.Data);

        cache.Store(2, 42, ExecutionPreviewImageResult.Success(new byte[] { 6 }));
        Assert.Equal(new byte[] { 4, 5 }, cache.TryGet(1, out frame, out _) ? frame.Data : Array.Empty<byte>());
        Assert.False(cache.TryGet(1, 43, out _, out _));
    }

    [Fact]
    public async Task OlderBackgroundFrameCannotOverwriteNewerLiveFrame()
    {
        using var captureStarted = new ManualResetEventSlim(false);
        using var releaseCapture = new ManualResetEventSlim(false);
        using var cache = new RecentScreenshotCache(
            _ =>
            {
                captureStarted.Set();
                releaseCapture.Wait();
                return ExecutionPreviewImageResult.Success(new byte[] { 1 });
            },
            now: () => new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        cache.BeginAttempt(1);

        try
        {
            Task background = cache.TryRefreshAsync(1, 42, CancellationToken.None);
            Assert.True(captureStarted.Wait(TimeSpan.FromSeconds(2)));

            cache.Store(1, 42, ExecutionPreviewImageResult.Success(new byte[] { 2 }));
            releaseCapture.Set();
            await background;

            Assert.True(cache.TryGet(1, out RecentScreenshotFrame frame, out _));
            Assert.Equal(new byte[] { 2 }, frame.Data);
        }
        finally
        {
            releaseCapture.Set();
        }
    }
}
