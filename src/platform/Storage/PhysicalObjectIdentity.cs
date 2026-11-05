using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Storage;

public sealed record PhysicalObjectIdentity(string VolumeSerial, string FileId)
{
    public static PhysicalObjectIdentity Read(string requestedPath)
    {
        using var handle = OpenRead(requestedPath, FileShare.ReadWrite | FileShare.Delete);
        return Read(handle);
    }

    internal static SafeFileHandle OpenRead(string requestedPath, FileShare share)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(requestedPath);
        var handle = CreateFile(PhysicalPathPolicy.NativePath(path), 0x80, (uint)share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        handle.Dispose();
        throw new IOException("physical_identity_open_failed", error);
    }

    public static PhysicalObjectIdentity Read(SafeFileHandle handle)
    {
        if (!ReadTag(handle, 9, out var tag, 8))
            throw new IOException("physical_identity_attributes_unavailable", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((tag.Attributes & 0x400) != 0) throw new IOException("filesystem_reparse_point_rejected");
        if (!ReadId(handle, 18, out var identity, 24))
            throw new IOException("physical_identity_unavailable", new Win32Exception(Marshal.GetLastWin32Error()));
        return new(identity.VolumeSerial.ToString("x16", CultureInfo.InvariantCulture),
            identity.Low.ToString("x16", CultureInfo.InvariantCulture) + identity.High.ToString("x16", CultureInfo.InvariantCulture));
    }

    internal static string EntryName(string path)
    {
        using var handle = OpenRead(path, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new StringBuilder(512);
        var length = GetFinalPathName(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity && length <= 32768)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathName(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("physical_entry_name_unavailable", new Win32Exception(Marshal.GetLastWin32Error()));
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(buffer.ToString()));
    }

    internal static void RequireSingleLink(string path)
    {
        using var handle = OpenRead(path, FileShare.ReadWrite | FileShare.Delete);
        if (!ReadStandard(handle, 1, out var info, (uint)Marshal.SizeOf<FileStandardInformation>()))
            throw new IOException("physical_link_count_unavailable", new Win32Exception(Marshal.GetLastWin32Error()));
        // Hard links do not have one exclusive namespace within the declared edit tree.
        if (info.Directory == 0 && info.NumberOfLinks != 1) throw new IOException("configuration_multiple_links_rejected");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { public ulong VolumeSerial; public ulong Low; public ulong High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation { public long AllocationSize; public long EndOfFile; public uint NumberOfLinks; public byte DeletePending; public byte Directory; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadId(SafeFileHandle handle, int informationClass, out FileIdentity information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadTag(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadStandard(SafeFileHandle handle, int informationClass, out FileStandardInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathName(SafeFileHandle handle, StringBuilder filename, uint capacity, uint flags);
}
