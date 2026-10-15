using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NexusPipeline.Platform.Storage;

internal sealed record VolumeSpace(string Identity, long AvailableBytes, long AllocationUnitBytes);

internal static class VolumeSpaceProbe
{
    internal static VolumeSpace Read(string destination)
    {
        string path = Path.GetFullPath(destination);
        while (!Directory.Exists(path) && !File.Exists(path))
            path = Path.GetDirectoryName(path) ?? throw new IOException("storage.volume_unknown");
        var volume = new StringBuilder(512);
        if (!GetVolumePathName(PayloadPathSafety.NativePath(path), volume, volume.Capacity)
            || !GetDiskFreeSpace(volume.ToString(), out uint sectors, out uint sectorBytes, out _, out _)
            || !GetDiskFreeSpaceEx(volume.ToString(), out ulong available, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        long unit = checked((long)sectors * sectorBytes);
        if (unit <= 0 || available > long.MaxValue) throw new IOException("storage.volume_invalid");
        return new(volume.ToString(), (long)available, unit);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, StringBuilder volume, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpace(string volume, out uint sectors, out uint sectorBytes, out uint freeClusters, out uint totalClusters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string volume, out ulong available, out ulong total, out ulong free);
}
