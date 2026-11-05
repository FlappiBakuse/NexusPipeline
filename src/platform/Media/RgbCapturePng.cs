namespace NexusPipeline.Platform.Media;

internal static class RgbCapturePng
{
    internal static void Verify(Stream input, int width, int height)
    {
        try
        {
            var info = BoundedCapturePng.Verify(input, allowRgba: false);
            if (info.Width != width || info.Height != height) throw new IOException("capture_rgb_png_invalid");
        }
        catch (IOException error) { throw new IOException("capture_rgb_png_invalid", error); }
    }
}
