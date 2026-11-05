using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.StorageTests;

internal static class ControlledJunction
{
    internal static void Create(string path, string target)
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("No test root")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(target).StartsWith(root, StringComparison.OrdinalIgnoreCase) || Directory.Exists(path))
            throw new IOException("Junction fixture must stay within its owned test root");
        Directory.CreateDirectory(path);
        Apply(path, target);
    }

    internal static void Attach(string path, string target)
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(target).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException("Junction fixture must use an empty owned directory");
        Apply(path, target);
    }

    private static void Apply(string path, string target)
    {
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + Path.GetFullPath(target));
        var display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var data = new byte[16 + substitute.Length + 2 + display.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
        BitConverter.GetBytes(checked((ushort)(data.Length - 8))).CopyTo(data, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(data, 8);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(data, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(data, 12);
        BitConverter.GetBytes(checked((ushort)display.Length)).CopyTo(data, 14);
        substitute.CopyTo(data, 16);
        display.CopyTo(data, 18 + substitute.Length);
        using var directory = CreateFileW(NexusPipeline.Platform.Storage.PhysicalPathPolicy.NativePath(path),
            0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (directory.IsInvalid || !DeviceIoControl(directory, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int bytesReturned, IntPtr overlapped);
}
