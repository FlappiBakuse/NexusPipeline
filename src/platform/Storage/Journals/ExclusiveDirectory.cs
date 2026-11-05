using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NexusPipeline.Platform.Storage.Journals;

internal static class ExclusiveDirectory
{
    internal static void Create(string path)
    {
        PhysicalPathPolicy.NormalizeWithoutLinks(path);
        if (!CreateDirectory(PhysicalPathPolicy.NativePath(path), IntPtr.Zero))
            throw new IOException("journal_directory_not_created", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
}
