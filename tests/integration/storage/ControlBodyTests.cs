using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

public sealed class ControlBodyTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Tiny Json Requests Use Small Read Windows With Or Without A Declared Length")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsTinyBodies(bool declared)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"value\":\"中文\"}");
        using var source = new ObservedStream(bytes);
        using var body = await ReadAsync(source, declared ? bytes.Length : null, 2 * 1024 * 1024);
        Assert.Equal("中文", body.Root.GetProperty("value").GetString());
        Assert.InRange(source.LargestWindow, 1, 8192);
    }

    [Theory(DisplayName = "Streamed Json Can Grow Within Its Byte Budget Without Treating Length Hints As Authority")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsGrowingBodies(bool inaccurateHint)
    {
        var expected = string.Concat(Enumerable.Repeat("中文-stream-", 7000));
        using var source = new ObservedStream(Encoding.UTF8.GetBytes("{\"value\":\"" + expected + "\"}"));
        using var body = await ReadAsync(source, inaccurateHint ? 3 : null, 100000);
        Assert.Equal(expected, body.Root.GetProperty("value").GetString());
    }

    [Theory(DisplayName = "Declared And Streamed Oversized Bodies Remain Rejected At The Actual Byte Boundary")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsOversizedBodies(bool declared)
    {
        using var source = new ObservedStream(Encoding.UTF8.GetBytes("{\"value\":\"" + new string('x', 1000) + "\"}"));
        var error = await Assert.ThrowsAsync<ControlRequestException>(() => ReadAsync(source, declared ? source.Length : null, 256));
        Assert.Equal("request_too_large", error.Message);
        Assert.Equal(413, error.Status);
        Assert.Equal(declared ? 0 : 257, source.BytesRead);
    }

    [Theory(DisplayName = "An Image Upload Reads Exact Bytes With A Capacity Proportional To Its Body")]
    [InlineData(1024, true, 8192)]
    [InlineData(1024, false, 8192)]
    [InlineData(200000, false, 262144)]
    [InlineData(8388608, true, 8388608)]
    [SupportedOSPlatform("windows")]
    public async Task BoundsImageCapacity(int length, bool declared, int maximumCapacity)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)0x7f);
        using var source = new ObservedStream(bytes);
        var context = new DefaultHttpContext();
        context.Request.Body = source;
        context.Request.ContentLength = declared ? length : null;
        using (var upload = await ExtensionUploadControl.ReadUploadAsync(context.Request, default))
            Assert.True(upload.Content.Span.SequenceEqual(bytes));
        output.WriteLine($"{length} bytes declared={declared}: actual pooled capacity {source.LargestCapacity}, largest read window {source.LargestWindow}");
        Assert.InRange(source.LargestCapacity, length, maximumCapacity);
    }

    [Theory(DisplayName = "Streaming Upload Rejects An Empty Body Or One Byte Beyond The Limit")]
    [InlineData(0)]
    [InlineData(8388609)]
    [SupportedOSPlatform("windows")]
    public async Task RejectsStreamedImageOutsideLimit(int length)
    {
        using var source = new ObservedStream(new byte[length]);
        var context = new DefaultHttpContext();
        context.Request.Body = source;
        var error = await Assert.ThrowsAsync<ControlRequestException>(() => ExtensionUploadControl.ReadUploadAsync(context.Request, default));
        Assert.Equal(413, error.Status);
        Assert.Equal(length, source.BytesRead);
        Assert.InRange(source.LargestCapacity, 1, 8388608);
    }

    [Fact(DisplayName = "A Cancelled Upload Clears An Array Already Used For Image Data")]
    [SupportedOSPlatform("windows")]
    public async Task ClearsCancelledImageBuffer()
    {
        using var cancel = new CancellationTokenSource();
        using var source = new ObservedStream(Enumerable.Repeat((byte)0xdf, 1024).ToArray(), cancel);
        var context = new DefaultHttpContext();
        context.Request.Body = source;
        context.Request.ContentLength = source.Length;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExtensionUploadControl.ReadUploadAsync(context.Request, cancel.Token));
        Assert.Equal(257, source.BytesRead);
        Assert.DoesNotContain(source.LastBuffer!, value => value != 0);
    }

    [Theory(DisplayName = "Two Overlapping Image Bodies Keep Independent Bytes And Clear Their Buffers")]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public async Task MeasuresConcurrentImageBodies(bool cancelSmall)
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jC1sAAAAASUVORK5CYII=");
        var nearLimit = new byte[8388607];
        png.CopyTo(nearLimit, 0);
        nearLimit[^1] = 0x7f;
        Assert.Equal(1, ExtensionImageBytes.Inspect("image/png", png).Width);
        Assert.Equal(1, ExtensionImageBytes.Inspect("image/png", nearLimit).Width);
        using var cancel = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var small = new ObservedStream(png, cancelSmall ? cancel : null, release.Task);
        using var large = new ObservedStream(nearLimit, firstReadGate: release.Task);
        var gcBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var smallRead = ReadImage(small, cancel.Token);
        var largeRead = ReadImage(large, CancellationToken.None);
        try
        {
            await Task.WhenAll(small.FirstRead!.Task, large.FirstRead!.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(smallRead.IsCompleted); Assert.False(largeRead.IsCompleted);
            Assert.NotSame(small.LastBuffer, large.LastBuffer);
        }
        finally { release.TrySetResult(); }

        using (var image = await largeRead) Assert.True(image.Content.Span.SequenceEqual(nearLimit));
        if (cancelSmall) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => smallRead);
        else using (var image = await smallRead) Assert.True(image.Content.Span.SequenceEqual(png));
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var collections = Enumerable.Range(0, 3).Select(generation => GC.CollectionCount(generation) - gcBefore[generation]).ToArray();
        Assert.Equal(nearLimit.Length, large.BytesRead);
        Assert.Equal(png.Length, small.BytesRead);
        Assert.DoesNotContain(small.LastBuffer!, value => value != 0);
        Assert.DoesNotContain(large.LastBuffer!, value => value != 0);
        output.WriteLine(JsonSerializer.Serialize(new { chain = "M14", cancelSmall, elapsedMilliseconds, allocatedBytes, collections,
            uploadedBytes = new[] { small.BytesRead, large.BytesRead }, clearedBufferBytes = new[] { small.LastBuffer!.Length, large.LastBuffer!.Length },
            counterScope = "test host process during two overlapping upload body calls" }));

        static Task<PooledControlBody> ReadImage(Stream source, CancellationToken cancellationToken)
        {
            var context = new DefaultHttpContext(); context.Request.Body = source; context.Request.ContentLength = source.Length;
            return ExtensionUploadControl.ReadUploadAsync(context.Request, cancellationToken);
        }
    }

    private static Task<ControlJsonBody> ReadAsync(Stream source, long? length, int maximum)
    {
        var context = new DefaultHttpContext(); context.Request.ContentType = "application/json";
        context.Request.Body = source; context.Request.ContentLength = length;
        return ControlRequestBody.ReadAsync(context, maximum);
    }

    private sealed class ObservedStream(byte[] bytes, CancellationTokenSource? cancelAfterRead = null, Task? firstReadGate = null) : MemoryStream(bytes)
    {
        internal int LargestWindow { get; private set; }
        internal int LargestCapacity { get; private set; }
        internal long BytesRead { get; private set; }
        internal byte[]? LastBuffer { get; private set; }
        internal TaskCompletionSource? FirstRead { get; } = firstReadGate is null ? null : new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LargestWindow = Math.Max(LargestWindow, buffer.Length);
            if (MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var storage))
            {
                LastBuffer = storage.Array;
                LargestCapacity = Math.Max(LargestCapacity, storage.Array!.Length);
            }
            if (FirstRead?.TrySetResult() == true) await firstReadGate!.WaitAsync(cancellationToken);
            var count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 257)], cancellationToken);
            BytesRead += count;
            cancelAfterRead?.Cancel();
            return count;
        }
    }
}
