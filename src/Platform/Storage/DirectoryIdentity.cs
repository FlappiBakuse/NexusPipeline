using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Storage;

internal static class DirectoryIdentity
{
    internal static string Read(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("directory_identity.windows_required");
        using var handle = CreateFile(path, 0, 7, nint.Zero, 3, 0x02000000, nint.Zero);
        return Read(handle);
    }

    internal static string Read(SafeFileHandle handle)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var identity) || (identity.FileAttributes & 0x400) != 0)
            throw new IOException("directory_identity.unavailable", Marshal.GetLastPInvokeError());
        return identity.VolumeSerialNumber.ToString("x8") + ":" + identity.FileIndexHigh.ToString("x8") + identity.FileIndexLow.ToString("x8");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
