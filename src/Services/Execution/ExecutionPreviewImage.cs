using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

internal sealed record ExecutionPreviewImageResult(bool Ok, byte[] Data, string Error)
{
    public static ExecutionPreviewImageResult Success(byte[] data) => new(true, data, "");

    public static ExecutionPreviewImageResult Failure(string error) => new(false, Array.Empty<byte>(), error);
}

/// <summary>将目标游戏客户区或模拟器 PNG 转换为 360p JPEG。所有 PC 捕获路径都绑定到指定 HWND 的客户区。</summary>
internal static class ExecutionPreviewImage
{
    private const int PreviewHeight = 360;
    private const long JpegQuality = 75L;
    private const uint PrintWindowClientOnly = 0x00000001;
    private const uint PrintWindowRenderFullContent = 0x00000002;

    internal static ExecutionPreviewImageResult CapturePc(int processId)
    {
        if (processId <= 0)
        {
            return ExecutionPreviewImageResult.Failure("正在等待游戏进程");
        }
        IntPtr window = SystemActions.FindVisibleWindow(processId);
        if (window == IntPtr.Zero)
        {
            return ExecutionPreviewImageResult.Failure("正在等待游戏窗口");
        }
        if (IsIconic(window))
        {
            return ExecutionPreviewImageResult.Failure("游戏窗口已最小化");
        }
        if (!GetClientRect(window, out RECT client) || client.Right <= client.Left || client.Bottom <= client.Top)
        {
            return ExecutionPreviewImageResult.Failure("游戏窗口客户区尚未就绪");
        }

        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        using Bitmap? printed = CaptureWithPrintWindow(window, width, height);
        if (printed is not null && !LooksBlank(printed))
        {
            return EncodeJpeg(printed);
        }

        using Bitmap? copied = CaptureClientRectangle(window, width, height);
        if (copied is not null && !LooksBlank(copied))
        {
            return EncodeJpeg(copied);
        }
        return ExecutionPreviewImageResult.Failure("游戏窗口暂未提供有效画面");
    }

    internal static ExecutionPreviewImageResult ConvertPng(byte[] png)
    {
        if (!EmulatorSupport.IsPng(png))
        {
            return ExecutionPreviewImageResult.Failure("模拟器未返回有效 PNG 截图");
        }
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            using Image image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            return EncodeJpeg(image);
        }
        catch (Exception ex)
        {
            return ExecutionPreviewImageResult.Failure($"模拟器截图解析失败：{ex.Message}");
        }
    }

    private static Bitmap? CaptureWithPrintWindow(IntPtr window, int width, int height)
    {
        try
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            IntPtr hdc = graphics.GetHdc();
            bool ok;
            try
            {
                ok = PrintWindow(window, hdc, PrintWindowClientOnly | PrintWindowRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
            if (ok)
            {
                return bitmap;
            }
            bitmap.Dispose();
        }
        catch
        {
        }
        return null;
    }

    private static Bitmap? CaptureClientRectangle(IntPtr window, int width, int height)
    {
        try
        {
            POINT origin = new();
            if (!ClientToScreen(window, ref origin))
            {
                return null;
            }
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksBlank(Bitmap bitmap)
    {
        int[,] points =
        {
            { bitmap.Width / 2, bitmap.Height / 2 },
            { bitmap.Width / 4, bitmap.Height / 4 },
            { bitmap.Width * 3 / 4, bitmap.Height / 4 },
            { bitmap.Width / 4, bitmap.Height * 3 / 4 },
            { bitmap.Width * 3 / 4, bitmap.Height * 3 / 4 },
        };
        for (int i = 0; i < points.GetLength(0); i++)
        {
            Color color = bitmap.GetPixel(
                Math.Clamp(points[i, 0], 0, bitmap.Width - 1),
                Math.Clamp(points[i, 1], 0, bitmap.Height - 1));
            if (color.R > 2 || color.G > 2 || color.B > 2)
            {
                return false;
            }
        }
        return true;
    }

    private static ExecutionPreviewImageResult EncodeJpeg(Image source)
    {
        try
        {
            int width = source.Width;
            int height = source.Height;
            if (width <= 0 || height <= 0)
            {
                return ExecutionPreviewImageResult.Failure("截图尺寸无效");
            }
            int outputHeight = height > PreviewHeight ? PreviewHeight : height;
            int outputWidth = height > PreviewHeight
                ? Math.Max(1, (int)Math.Round(width * (double)PreviewHeight / height))
                : width;
            using var resized = new Bitmap(outputWidth, outputHeight, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(resized))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, outputWidth, outputHeight));
            }
            ImageCodecInfo? codec = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(item => item.FormatID == ImageFormat.Jpeg.Guid);
            if (codec is null)
            {
                return ExecutionPreviewImageResult.Failure("系统缺少 JPEG 编码器");
            }
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
            using var output = new MemoryStream();
            resized.Save(output, codec, parameters);
            return ExecutionPreviewImageResult.Success(output.ToArray());
        }
        catch (Exception ex)
        {
            return ExecutionPreviewImageResult.Failure($"截图压缩失败：{ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
