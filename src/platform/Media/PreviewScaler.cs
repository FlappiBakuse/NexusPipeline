using System.Runtime.Versioning;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using NexusPipeline.Core.Media;

namespace NexusPipeline.Platform.Media;

[SupportedOSPlatform("windows")]
internal static class PreviewScaler
{
    internal static async ValueTask<PreviewImage> ScaleAsync(PreviewImage image, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (image.Height <= 360) return image;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            throw new PlatformNotSupportedException("media_preview_scaling_unavailable");
        var height = 360;
        var width = Math.Max(1, (int)Math.Round(image.Width * (double)height / image.Height));
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(image.Png.ToArray());
            await writer.StoreAsync().AsTask(cancellation).ConfigureAwait(false);
            writer.DetachStream();
        }
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.PngDecoderId, input).AsTask(cancellation).ConfigureAwait(false);
        if (decoder.PixelWidth != image.Width || decoder.PixelHeight != image.Height)
            throw new IOException("media_preview_dimensions_changed");
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellation).ConfigureAwait(false);
        var bytes = pixels.DetachPixelData();
        if (bytes.Length != checked(width * height * 4)) throw new IOException("media_preview_pixels_invalid");
        using var output = new MemoryStream();
        PngScanlineEncoder.Write(output, width, height, (row, target) =>
        {
            cancellation.ThrowIfCancellationRequested();
            bytes.AsSpan(row * width * 4, width * 4).CopyTo(target);
        });
        cancellation.ThrowIfCancellationRequested();
        return new(width, height, output.GetBuffer().AsSpan(0, checked((int)output.Length)), image.CapturedAt);
    }
}
