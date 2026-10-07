using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Storage;

internal static class VerifiedFileDeletion
{
    internal static void Delete(string path, long sizeBytes, string sha256)
    {
        PayloadPathSafety.RequireLinkFree(path);
        using SafeFileHandle handle = CreateFileW(path, 0x80010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("owned_file_open_failed", new Win32Exception(Marshal.GetLastWin32Error()));
        if (!GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInfo attributes, Marshal.SizeOf<FileAttributeTagInfo>())
            || (attributes.Attributes & 0x410) != 0)
            throw new IOException("owned_file_type_changed");
        using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length != sizeBytes || Convert.ToHexStringLower(SHA256.HashData(stream)) != sha256)
            throw new IOException("owned_file_bytes_changed");
        // DELETE access and an exclusive handle bind the check and removal to the same file.
        int disposition = 1;
        if (!SetFileInformationByHandle(handle, 4, ref disposition, sizeof(int)))
            throw new IOException("owned_file_delete_failed", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string file, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref int information, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { internal uint Attributes; internal uint ReparseTag; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInfo information, int size);
}
