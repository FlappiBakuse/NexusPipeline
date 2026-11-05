using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Media;

internal sealed record WindowCaptureRequest(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid RequestId, [property: JsonRequired] Guid InstanceId,
    [property: JsonRequired] Guid AttemptId, [property: JsonRequired] ProcessIdentity Target,
    [property: JsonRequired] long WindowHandle)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FrameCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CaptureMethod { get; init; }
}

[SupportedOSPlatform("windows")]
public static class WindowCaptureWorker
{
    private const uint RedrawFrameFlags = 0x101;
    internal static JsonSerializerOptions Options { get; } = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static async Task<int> RunAsync()
    {
        try
        {
            using var bytes = new MemoryStream();
            using (var input = Console.OpenStandardInput())
            {
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await input.ReadAsync(buffer).ConfigureAwait(false);
                    if (count == 0) break;
                    if (bytes.Length + count > 32768) throw new IOException("capture_request_byte_limit");
                    bytes.Write(buffer, 0, count);
                }
            }
            using var parsed = StrictJson.Parse(bytes.ToArray());
            var request = parsed.RootElement.Deserialize<WindowCaptureRequest>(Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(request, Options)) ||
                request.Format != "nexus.media.window-capture-request" ||
                !(request.Version == 1 && request.FrameCount is null ||
                    request.Version == 2 && request.FrameCount is >= 2 and <= 200) || request.RequestId == Guid.Empty ||
                request.InstanceId == Guid.Empty || request.AttemptId == Guid.Empty || request.Target is null ||
                request.Target.ProcessId <= 0 || request.WindowHandle <= 0 || request.CaptureMethod is not (null or "gdi" or "screenDc" or "framePool")) throw Invalid();
            using var process = JobNative.OpenProcess(0x101000, false, request.Target.ProcessId);
            if (process.IsInvalid || JobNative.WaitForSingleObject(process, 0) != 258 ||
                (request.Version == 1 ? ProcessIdentityReader.Read(process, request.Target.ProcessId) != request.Target :
                    ProcessIdentityReader.ReadMetadata(process, request.Target.ProcessId) != ProcessIdentityMetadata.From(request.Target)))
                throw Invalid();
            using var image = request.Version == 2 ? new FileStream(request.Target.ImagePath, FileMode.Open,
                FileAccess.Read, FileShare.Read) : null;
            var imageObject = image is null ? null : PhysicalObjectIdentity.Read(image.SafeFileHandle);
            if (image is not null && Convert.ToHexStringLower(SHA256.HashData(image)) != request.Target.ImageSha256) throw Invalid();
            var window = new IntPtr(request.WindowHandle);
            var dimensions = Dimensions(window, request.Target.ProcessId);
            using var bitmap = new WindowBitmap(dimensions.Width, dimensions.Height);
            using var output = Console.OpenStandardOutput();
            if (request.Version == 2)
            {
                WriteBatch(output, request, process, image!, imageObject!, window, bitmap, dimensions);
                output.Flush(); return 0;
            }
            bitmap.Capture(window, dimensions.Width, dimensions.Height, request.CaptureMethod ?? "printWindow");
            RequireWindow(request, process, window, dimensions);
            var prefix = new byte[32]; "NXSCAP01"u8.CopyTo(prefix); request.RequestId.TryWriteBytes(prefix.AsSpan(8, 16));
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(24), dimensions.Width);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(28), dimensions.Height); output.Write(prefix);
            PngScanlineEncoder.Write(output, dimensions.Width, dimensions.Height, bitmap.ReadRow);
            output.Flush();
            RequireWindow(request, process, window, dimensions);
            return 0;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or Win32Exception or UnauthorizedAccessException or COMException or OperationCanceledException)
        {
            Console.Error.WriteLine(error.Message is "capture_composition_unavailable" or "capture_window_offscreen" or "capture_window_occluded"
                ? error.Message : "capture_window_rejected");
            return 1;
        }
    }

    private static void WriteBatch(Stream output, WindowCaptureRequest request, SafeKernelHandle process,
        FileStream image, PhysicalObjectIdentity imageObject, IntPtr window, WindowBitmap bitmap,
        (int Width, int Height) dimensions)
    {
        var count = request.FrameCount ?? throw Invalid();
        using var frame = new BoundedFrameBuffer();
        long total = 0;
        for (var index = 0; index < count; index++)
        {
            RequireWindow(request, process, window, dimensions, image, imageObject);
            // A synchronous redraw prevents successive PrintWindow calls from reusing the window's cached pixels.
            if (!RedrawWindow(window, IntPtr.Zero, IntPtr.Zero, RedrawFrameFlags))
                throw new IOException("capture_window_unavailable");
            RequireWindow(request, process, window, dimensions, image, imageObject);
            bitmap.Capture(window, dimensions.Width, dimensions.Height, request.CaptureMethod ?? "printWindow");
            RequireWindow(request, process, window, dimensions, image, imageObject);
            frame.SetLength(0); frame.Position = 0;
            PngScanlineEncoder.Write(frame, dimensions.Width, dimensions.Height, bitmap.ReadRow);
            total = checked(total + frame.Length);
            if (total > 67_108_864) throw new IOException("capture_batch_byte_limit");
            var header = new byte[40]; "NXSCAP02"u8.CopyTo(header);
            request.RequestId.TryWriteBytes(header.AsSpan(8, 16));
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), index);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), dimensions.Width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), dimensions.Height);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36), checked((int)frame.Length));
            output.Write(header); output.Write(frame.GetBuffer().AsSpan(0, checked((int)frame.Length)));
        }
        RequireWindow(request, process, window, dimensions, image, imageObject);
    }

    private static void RequireWindow(WindowCaptureRequest request, SafeKernelHandle process,
        IntPtr window, (int Width, int Height) dimensions, FileStream? image = null, PhysicalObjectIdentity? imageObject = null)
    {
        if (dimensions != Dimensions(window, request.Target.ProcessId) || JobNative.WaitForSingleObject(process, 0) != 258 ||
            (image is null ? ProcessIdentityReader.Read(process, request.Target.ProcessId) != request.Target :
                ProcessIdentityReader.ReadMetadata(process, request.Target.ProcessId) != ProcessIdentityMetadata.From(request.Target) ||
                imageObject != PhysicalObjectIdentity.Read(image.SafeFileHandle))) throw Invalid();
    }

    internal sealed class BoundedFrameBuffer : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > 8_388_608) throw new IOException("capture_frame_byte_limit");
            base.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > 8_388_608 - Length) throw new IOException("capture_frame_byte_limit");
            base.Write(buffer, offset, count);
        }
        public override void WriteByte(byte value)
        {
            if (Length >= 8_388_608) throw new IOException("capture_frame_byte_limit");
            base.WriteByte(value);
        }
    }

    internal static (int Width, int Height) Dimensions(IntPtr window, int processId)
    {
        if (!IsWindowVisible(window) || IsIconic(window) || GetAncestor(window, 2) != window ||
            GetWindowThreadProcessId(window, out var owner) == 0 || owner != processId || !GetClientRect(window, out var rect)) throw Invalid();
        var width = checked(rect.Right - rect.Left); var height = checked(rect.Bottom - rect.Top);
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 33554432) throw Invalid();
        return (width, height);
    }

    private static IOException Invalid() => new("capture_window_identity_unbound");

    private sealed class WindowBitmap : IDisposable
    {
        internal IntPtr Context { get; }
        private readonly IntPtr _bitmap, _previous, _bits;
        private readonly int _stride;
        internal WindowBitmap(int width, int height)
        {
            _stride = checked(width * 4); Context = CreateCompatibleDC(IntPtr.Zero);
            if (Context == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32,
                    SizeImage = checked((uint)(_stride * height)) };
                _bitmap = CreateDIBSection(Context, ref header, 0, out _bits, IntPtr.Zero, 0);
                if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
                _previous = SelectObject(Context, _bitmap);
                if (_previous == IntPtr.Zero || _previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            catch { Dispose(); throw; }
        }
        internal void Capture(IntPtr window, int width, int height, string method)
        {
            if (method != "framePool") { WindowCaptureMethods.Capture(window, Context, width, height, method); return; }
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) throw new IOException("capture_frame_pool_unavailable");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var pixels = FramePoolCapture.CaptureClientAsync(window, width, height, deadline.Token).GetAwaiter().GetResult();
            if (pixels.Length != checked(_stride * height)) throw new IOException("capture_frame_pool_dimensions_changed");
            Marshal.Copy(pixels, 0, _bits, pixels.Length);
        }
        internal void ReadRow(int y, byte[] destination) => Marshal.Copy(IntPtr.Add(_bits, checked(y * _stride)), destination, 0, _stride);
        public void Dispose()
        {
            if (_previous != IntPtr.Zero && _previous != new IntPtr(-1)) SelectObject(Context, _previous);
            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            if (Context != IntPtr.Zero) DeleteDC(Context);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, BitCount;
        internal uint Compression, SizeImage; internal int XPelsPerMeter, YPelsPerMeter; internal uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RedrawWindow(
        IntPtr window, IntPtr rectangle, IntPtr update, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr device);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr device, ref BitmapHeader bitmap, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr device, IntPtr item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr device);
}
