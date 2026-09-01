using System.Drawing;
using System.Drawing.Imaging;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class RunScreenshotStoreTests
{
    [Fact]
    public async Task StoreUsesPerAttemptFifoCapacityAndAttemptScopedSelection()
    {
        byte[] jpeg = MakeJpeg(640, 480);
        int calls = 0;
        using var store = new RunScreenshotStore((attempt, trigger, _) =>
        {
            calls++;
            return Task.FromResult(RunScreenshotCaptureResult.Success(jpeg, "pc"));
        });

        for (int index = 1; index <= RunScreenshotStore.Capacity + 1; index++)
        {
            RunScreenshot? captured = await store.CaptureAsync(1, "judge-manual", CancellationToken.None);
            Assert.NotNull(captured);
        }
        for (int index = 1; index <= 2; index++)
        {
            RunScreenshot? captured = await store.CaptureAsync(2, "judge-manual", CancellationToken.None);
            Assert.NotNull(captured);
        }

        Assert.Equal(RunScreenshotStore.Capacity + 2, store.Metadata.Count);
        Assert.Equal(RunScreenshotStore.Capacity, store.MetadataForAttempt(1).Count);
        Assert.Equal(2, store.MetadataForAttempt(1)[0].Ordinal);
        Assert.Equal(9, store.MetadataForAttempt(1)[^1].Ordinal);
        Assert.Equal(2, store.MetadataForAttempt(2).Count);
        Assert.Equal(10, store.MetadataForAttempt(2)[0].Ordinal);
        Assert.Equal(11, store.MetadataForAttempt(2)[^1].Ordinal);
        Assert.Equal("screenshot-0000000009", store.SelectForNotification(1, null)?.Id);
        Assert.Equal("screenshot-0000000011", store.SelectForNotification(2, null)?.Id);
        Assert.Null(store.SelectForNotification("screenshot-0000000001"));
        Assert.Null(store.SelectForNotification(1, "screenshot-0000000010"));
        Assert.Equal("screenshot-0000000002", store.SelectForNotification(1, "screenshot-0000000002")?.Id);
        Assert.Equal("screenshot-0000000011", store.SelectForNotification(null)?.Id);
        Assert.Equal(RunScreenshotStore.Capacity + 3, calls);
    }

    [Fact]
    public async Task CaptureFailurePreservesExistingImagesAndDisposeClearsPool()
    {
        byte[] jpeg = MakeJpeg(320, 240);
        bool fail = false;
        using var store = new RunScreenshotStore((_, _, _) =>
            Task.FromResult(fail
                ? RunScreenshotCaptureResult.Failure("pc", "window unavailable")
                : RunScreenshotCaptureResult.Success(jpeg, "pc")));

        RunScreenshot? first = await store.CaptureAsync(1, "keyword-success", CancellationToken.None);
        Assert.NotNull(first);
        fail = true;
        Assert.Null(await store.CaptureAsync(1, "judge-success", CancellationToken.None));
        Assert.Single(store.Metadata);
        Assert.Equal(first!.Id, store.SelectForNotification(1, null)?.Id);

        store.Dispose();
        Assert.Empty(store.Metadata);
        Assert.Null(store.SelectForNotification(null));
    }

    private static byte[] MakeJpeg(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var stream = new MemoryStream())
        {
            graphics.Clear(Color.CornflowerBlue);
            bitmap.Save(stream, ImageFormat.Jpeg);
            return stream.ToArray();
        }
    }
}
