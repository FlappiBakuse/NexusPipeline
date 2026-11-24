namespace NexusPipeline.Platform.Media;

internal static class RgbCapturePng
{
    internal static CapturePngInfo Verify(Stream input, int width, int height)
    {
        try
        {
            var info = BoundedCapturePng.Verify(input, allowRgba: false);
            if (info.Width != width || info.Height != height) throw new IOException("capture_rgb_png_invalid");
            return info;
        }
        catch (IOException error) { throw new IOException("capture_rgb_png_invalid", error); }
    }
}
