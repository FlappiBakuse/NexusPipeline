using System.Drawing;
using System.Drawing.Imaging;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class RunScreenshotStoreTests
{
    [Fact]
    public async Task StoreUsesFifoCapacityAndLatestDefaultSelection()
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
            RunScreenshot? captured = await store.CaptureAsync(index, "judge-manual", CancellationToken.None);
            Assert.NotNull(captured);
        }

        Assert.Equal(RunScreenshotStore.Capacity, store.Metadata.Count);
        Assert.Equal(2, store.Metadata[0].Ordinal);
        Assert.Equal(17, store.Metadata[^1].Ordinal);
        Assert.Equal("screenshot-0000000017", store.SelectForNotification(null)?.Id);
        Assert.Null(store.SelectForNotification("screenshot-0000000001"));
        Assert.Equal("screenshot-0000000002", store.SelectForNotification("screenshot-0000000002")?.Id);
        Assert.Equal(RunScreenshotStore.Capacity + 1, calls);
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
        Assert.Equal(first!.Id, store.SelectForNotification(null)?.Id);

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
