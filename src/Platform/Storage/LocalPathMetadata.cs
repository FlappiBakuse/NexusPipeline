using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Storage;

/// <summary>Read local file metadata through an access-checked, non-following handle.</summary>
internal static class LocalPathMetadata
{
    internal static FileAttributes ReadAttributes(string path)
    {
        // READ_ATTRIBUTES only: no contents, enumeration, writes or execution.
        // BACKUP_SEMANTICS permits directory handles; OPEN_REPARSE_POINT keeps
        // the leaf link itself visible instead of opening its destination.
        using SafeFileHandle handle = CreateFileW(path, 0x80, 0x7, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) Throw(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 0, out BasicInformation information,
                (uint)Marshal.SizeOf<BasicInformation>()))
            Throw(Marshal.GetLastWin32Error());
        return (FileAttributes)information.Attributes;
    }

    private static void Throw(int error)
    {
        throw error switch
        {
            2 => new FileNotFoundException(),
            3 => new DirectoryNotFoundException(),
            5 => new UnauthorizedAccessException(),
            50 or 87 or 123 => new NotSupportedException(),
            _ => new IOException(new Win32Exception(error).Message),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
        uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out BasicInformation information, uint size);
}
