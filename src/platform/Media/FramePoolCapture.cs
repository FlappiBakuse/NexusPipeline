using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace NexusPipeline.Platform.Media;

[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.22621")]
internal static class FramePoolCapture
{
    internal static async Task<byte[]> CaptureClientAsync(nint window, int width, int height, CancellationToken cancellation)
    {
        var before = Bounds(window, width, height);
        var captured = await CaptureAsync(window, cancellation).ConfigureAwait(false);
        if (before != Bounds(window, width, height) || captured.Width != before.Right - before.Left || captured.Height != before.Bottom - before.Top)
            throw new IOException("capture_frame_pool_window_changed");
        var x = before.X - before.Left; var y = before.Y - before.Top;
        if (x < 0 || y < 0 || x + width > captured.Width || y + height > captured.Height)
            throw new IOException("capture_frame_pool_client_unbound");
        var pixels = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++)
            System.Buffer.BlockCopy(captured.Pixels, checked(((y + row) * captured.Width + x) * 4), pixels, checked(row * width * 4), checked(width * 4));
        return pixels;
    }

    private static (int Left, int Top, int Right, int Bottom, int X, int Y) Bounds(nint window, int width, int height)
    {
        var previous = SetDpiContext(new nint(-4));
        if (previous == 0) throw new IOException("capture_frame_pool_dpi_unavailable");
        try
        {
            var origin = new Point();
            if (WindowAttribute(window, 9, out var frame, Marshal.SizeOf<Rect>()) < 0 || !ClientToScreen(window, ref origin) ||
                !GetClientRect(window, out var client) || client.Right - client.Left != width || client.Bottom - client.Top != height)
                throw new IOException("capture_frame_pool_client_unbound");
            return (frame.Left, frame.Top, frame.Right, frame.Bottom, origin.X, origin.Y);
        }
        finally { _ = SetDpiContext(previous); }
    }
    internal static async Task<(byte[] Pixels, int Width, int Height)> CaptureAsync(nint window, CancellationToken cancellation)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new IOException("capture_frame_pool_unavailable");
        using var device = CreateDevice();
        var item = CreateItem(window);
        if (item.Size.Width is < 1 or > 8192 || item.Size.Height is < 1 or > 8192 || (long)item.Size.Width * item.Size.Height > 33554432)
            throw new IOException("capture_frame_pool_dimensions_invalid");
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        Direct3D11CaptureFrame? pending = null;
        var closed = false;
        void Arrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                lock (gate)
                {
                    if (closed || pending is not null) return;
                    pending = sender.TryGetNextFrame();
                    if (pending is not null) completion.TrySetResult();
                }
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
        pool.FrameArrived += Arrived;
        try
        {
            session.StartCapture();
            await completion.Task.WaitAsync(cancellation).ConfigureAwait(false);
            Direct3D11CaptureFrame actual;
            lock (gate) { closed = true; actual = pending!; pending = null; }
            using var frame = actual;
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask(cancellation).ConfigureAwait(false);
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.PixelWidth != item.Size.Width || bitmap.PixelHeight != item.Size.Height ||
                frame.ContentSize.Width != item.Size.Width || frame.ContentSize.Height != item.Size.Height)
                throw new IOException("capture_frame_pool_dimensions_changed");
            var bytes = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
            var buffer = new global::Windows.Storage.Streams.Buffer(checked((uint)bytes.Length));
            bitmap.CopyToBuffer(buffer);
            using var reader = DataReader.FromBuffer(buffer); reader.ReadBytes(bytes);
            return (bytes, bitmap.PixelWidth, bitmap.PixelHeight);
        }
        finally
        {
            pool.FrameArrived -= Arrived;
            lock (gate) { closed = true; pending?.Dispose(); pending = null; }
        }
    }

    private static GraphicsCaptureItem CreateItem(nint window)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interopId = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in interopId, out var interop));
        try
        {
            var function = Marshal.GetDelegateForFunctionPointer<CreateForWindow>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(interop), 3 * IntPtr.Size));
            var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            Marshal.ThrowExceptionForHR(function(interop, window, ref itemId, out var item));
            try { return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(interop); }
    }

    private static IDirect3DDevice CreateDevice()
    {
        Marshal.ThrowExceptionForHR(CreateNativeDevice(0, 1, 0, 0x20, 0, 0, 7, out var device, out _, out var context));
        try
        {
            var id = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in id, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(WrapDevice(dxgi, out var direct));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(direct); }
                finally { Marshal.Release(direct); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(context); Marshal.Release(device); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateForWindow(nint self, nint window, ref Guid iid, out nint item);
    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")] private static extern int CreateNativeDevice(nint adapter, uint driverType, nint software, uint flags,
        nint levels, uint count, uint sdkVersion, out nint device, out uint featureLevel, out nint context);
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")] private static extern int WrapDevice(nint device, out nint result);
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int WindowAttribute(nint window, uint attribute, out Rect value, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out Rect bounds);
    [DllImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext")] private static extern nint SetDpiContext(nint context);
}
