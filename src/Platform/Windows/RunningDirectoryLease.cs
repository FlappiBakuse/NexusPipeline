using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Windows;

internal static class RunningDirectoryLease
{
    internal static SafeFileHandle Acquire(string directory)
    {
        var handle = CreateFileW(Path.GetFullPath(directory), 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, "无法保护运行中的软件目录，请检查目录是否正在移动。");
    }

    // A directory handle without delete sharing prevents renaming the running installation.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}
